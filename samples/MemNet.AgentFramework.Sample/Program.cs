using System.ComponentModel;
using System.ClientModel;
using System.Text;
using System.Text.Json;
using Azure.AI.OpenAI;
using Azure.Identity;
using MemNet.AgentMemory;
using MemNet.Client;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;

var azureEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
var useAzureOpenAi = !string.IsNullOrWhiteSpace(azureEndpoint);
var modelName = useAzureOpenAi
    ? Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_NAME") ?? "gpt-5.1"
    : Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-5.1";

var memNetBaseUrl = Environment.GetEnvironmentVariable("MEMNET_BASE_URL") ?? "http://localhost:5071";
var tenantId = Environment.GetEnvironmentVariable("MEMNET_TENANT_ID") ?? "tenant-demo";
var userId = Environment.GetEnvironmentVariable("MEMNET_USER_ID") ?? "user-demo";
var serviceId = Environment.GetEnvironmentVariable("MEMNET_SERVICE_ID") ?? "memory-agent-sample";

using var memClient = new MemNetClient(new MemNetClientOptions
{
    BaseAddress = new Uri(memNetBaseUrl),
    ServiceId = serviceId
});

var scope = new MemNetScope(tenantId, userId);
var memory = new AgentMemory(memClient, new AgentMemoryPolicy("default", Array.Empty<MemorySlotPolicy>()));
var memorySessionContext = new MemorySessionContext(memClient, memory, scope);
await memorySessionContext.PrimeAsync();
var memoryTools = new MemoryTools(memory, scope, memorySessionContext);
var chatClient = useAzureOpenAi
    ? CreateAzureOpenAiChatClient(azureEndpoint!, modelName)
    : CreateOpenAiChatClient(modelName);
var providerLabel = useAzureOpenAi ? "azure_openai" : "openai";

AIAgent agent = chatClient.AsAIAgent(
        name: "memory-agent",
        instructions:
            "You are a memory agent. " +
            "Keep responses concise by default (target <= 120 words, unless user explicitly asks for detail). " +
            "Do not dump long blocks of text unless requested. " +
            "Use memory tools to persist important facts in markdown files and recall prior event digests. " +
            "Prefer: profile.md, long_term_memory.md, projects/{project_name}.md.",
        tools:
        [
            AIFunctionFactory.Create(memoryTools.MemoryRecallAsync, name: "memory_recall"),
            AIFunctionFactory.Create(memoryTools.MemoryListFilesAsync, name: "memory_list_files"),
            AIFunctionFactory.Create(memoryTools.MemoryLoadFileAsync, name: "memory_load_file"),
            AIFunctionFactory.Create(memoryTools.MemoryPatchFileAsync, name: "memory_patch_file"),
            AIFunctionFactory.Create(memoryTools.MemoryWriteFileAsync, name: "memory_write_file")
        ]);

var health = await memClient.GetServiceStatusAsync();
Console.WriteLine($"Connected to mem.net: {health.Service} ({health.Status})");
Console.WriteLine($"LLM provider: {providerLabel}; model/deployment: {modelName}");
Console.WriteLine("Memory preload policy: prime once per session; refresh/reinject after profile/long-term/project updates.");
Console.WriteLine("Type messages. Use /exit to quit.\n");

var session = await agent.CreateSessionAsync();

while (true)
{
    Console.Write("> ");
    var input = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(input))
    {
        continue;
    }

    if (string.Equals(input.Trim(), "/exit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    var responseText = new StringBuilder();

    Console.WriteLine();
    var turnMessages = memorySessionContext.BuildTurnMessages(input);
    await foreach (var update in agent.RunStreamingAsync(turnMessages, session))
    {
        if (!string.IsNullOrEmpty(update.Text))
        {
            Console.Write(update.Text);
            responseText.Append(update.Text);
        }
    }
    Console.WriteLine();
    Console.WriteLine();

    await WriteTurnDigestAsync(memory, scope, serviceId, input, responseText.ToString());
}

return;

static async Task WriteTurnDigestAsync(AgentMemory memory, MemNetScope scope, string serviceId, string userText, string assistantText)
{
    var digest = $"User: {Clamp(userText.Trim(), 280)} | Assistant: {Clamp(assistantText.Trim(), 320)}";
    var keywords = BuildKeywords(userText).ToArray();

    var evt = new EventDigest(
        EventId: Guid.NewGuid().ToString("N"),
        TenantId: scope.TenantId,
        UserId: scope.UserId,
        ServiceId: serviceId,
        Timestamp: DateTimeOffset.UtcNow,
        SourceType: "chat.turn",
        Digest: digest,
        Keywords: keywords,
        ProjectIds: Array.Empty<string>(),
        Evidence: null);

    await memory.RememberAsync(scope, new RememberRequest(evt));
}

static IEnumerable<string> BuildKeywords(string input)
{
    return input
        .Split(new[] { ' ', '\t', '\r', '\n', ',', '.', ':', ';', '!', '?', '(', ')', '[', ']' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.Trim().ToLowerInvariant())
        .Where(x => x.Length >= 4)
        .Distinct(StringComparer.Ordinal)
        .Take(8);
}

static string Clamp(string value, int maxChars)
{
    return value.Length <= maxChars ? value : value[..maxChars];
}

static string RequireEnv(string name)
{
    return Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Set environment variable '{name}'.");
}

static IChatClient CreateOpenAiChatClient(string modelName)
{
    return new OpenAIClient(RequireEnv("OPENAI_API_KEY"))
        .GetChatClient(modelName)
        .AsIChatClient();
}

static IChatClient CreateAzureOpenAiChatClient(string endpoint, string deploymentName)
{
    var apiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");

    var client = string.IsNullOrWhiteSpace(apiKey)
        ? new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
        : new AzureOpenAIClient(new Uri(endpoint), new ApiKeyCredential(apiKey));

    return client.GetChatClient(deploymentName).AsIChatClient();
}

internal sealed class MemoryTools
{
    private readonly AgentMemory _memory;
    private readonly MemNetScope _scope;
    private readonly MemorySessionContext _sessionContext;

    public MemoryTools(AgentMemory memory, MemNetScope scope, MemorySessionContext sessionContext)
    {
        _memory = memory;
        _scope = scope;
        _sessionContext = sessionContext;
    }

    [Description("Search event digests in long-term memory and return top results.")]
    public async Task<string> MemoryRecallAsync(
        [Description("Natural-language query for event recall.")] string query,
        [Description("Maximum number of hits. Recommended range: 1-20.")] int topK = 8,
        CancellationToken cancellationToken = default)
    {
        topK = Math.Clamp(topK, 1, 20);
        LogToolCall("memory_recall", $"query=\"{ClampForLog(query, 80)}\", topK={topK}");
        var results = await _memory.MemoryRecallAsync(_scope, query, topK, cancellationToken);
        if (results.Count == 0)
        {
            LogToolResult("memory_recall", "0 results");
            return "No event memories matched that query.";
        }

        LogToolResult("memory_recall", $"{results.Count} result(s)");
        var sb = new StringBuilder();
        for (var i = 0; i < results.Count; i++)
        {
            var item = results[i];
            sb.Append('[').Append(i + 1).Append("] ")
                .Append(item.Timestamp.ToString("O"))
                .Append(" | ")
                .Append(item.SourceType)
                .Append(" | ")
                .AppendLine(item.Digest);
        }

        return sb.ToString();
    }

    [Description("List existing memory files. Optionally filter by path prefix.")]
    public async Task<string> MemoryListFilesAsync(
        [Description("Optional path prefix. Example: projects/.")] string? prefix = null,
        [Description("Optional maximum files to return. Recommended range: 1-200.")] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        var normalizedPrefix = string.IsNullOrWhiteSpace(prefix) ? null : prefix;
        LogToolCall("memory_list_files", $"prefix=\"{normalizedPrefix ?? ""}\", limit={limit}");

        var files = await _memory.MemoryListFilesAsync(_scope, normalizedPrefix, limit, cancellationToken);
        if (!string.IsNullOrWhiteSpace(normalizedPrefix)
            && normalizedPrefix.StartsWith("projects/", StringComparison.OrdinalIgnoreCase))
        {
            _sessionContext.UpdateProjectCatalog(files);
        }

        if (files.Count == 0)
        {
            LogToolResult("memory_list_files", "0 results");
            return "No files matched that prefix.";
        }

        var sb = new StringBuilder();
        foreach (var file in files)
        {
            sb.Append("- ")
                .Append(file.Path)
                .Append(" (last_modified_utc=")
                .Append(file.LastModifiedUtc.ToString("O"))
                .AppendLine(")");
        }

        var result = sb.ToString().TrimEnd();
        LogToolResult("memory_list_files", result);
        return result;
    }

    [Description("Load a memory file by path and return its full text content.")]
    public async Task<string> MemoryLoadFileAsync(
        [Description("File path, for example profile.md.")] string path,
        CancellationToken cancellationToken = default)
    {
        LogToolCall("memory_load_file", $"path=\"{path}\"");
        try
        {
            var file = await _memory.MemoryLoadFileAsync(_scope, path, cancellationToken);
            var result = BuildFileContentResult(path, file.Content, "Loaded");
            LogToolResult("memory_load_file", result);
            return file.Content;
        }
        catch (MemNetApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            LogToolResult("memory_load_file", "not_found");
            return $"File not found: {path}";
        }
    }

    [Description("Patch text in a memory file by replacing old_text with new_text.")]
    public async Task<string> MemoryPatchFileAsync(
        [Description("File path, for example profile.md, long_term_memory.md, projects/{project_name}.md.")] string path,
        [Description("Exact existing text to match.")] string old_text,
        [Description("Replacement text.")] string new_text,
        [Description("1-based occurrence index when old_text appears multiple times. Optional.")] int? occurrence = null,
        CancellationToken cancellationToken = default)
    {
        LogToolCall(
            "memory_patch_file",
            $"path=\"{path}\", old=\"{ClampForLog(old_text, 60)}\", new=\"{ClampForLog(new_text, 60)}\", occurrence={occurrence?.ToString() ?? "null"}");
        try
        {
            var file = await _memory.MemoryPatchFileAsync(
                _scope,
                path,
                [new MemoryPatchEdit(old_text, new_text, occurrence)],
                cancellationToken: cancellationToken);

            var result = BuildFileContentResult(path, file.Content, "Patched");
            LogToolResult("memory_patch_file", result);
            _sessionContext.MarkFileUpdated(path, file.Content);
            return result;
        }
        catch (MemNetApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            LogToolResult("memory_patch_file", "not_found");
            return $"File not found: {path}";
        }
    }

    [Description("Write full text content to a memory file (create or replace).")]
    public async Task<string> MemoryWriteFileAsync(
        [Description("File path, for example profile.md, long_term_memory.md, projects/{project_name}.md.")] string path,
        [Description("Full markdown content to write.")] string content,
        CancellationToken cancellationToken = default)
    {
        LogToolCall("memory_write_file", $"path=\"{path}\", chars={content.Length}");
        var file = await _memory.MemoryWriteFileAsync(_scope, path, content, cancellationToken: cancellationToken);
        var result = BuildFileContentResult(path, file.Content, "Wrote");
        LogToolResult("memory_write_file", result);
        _sessionContext.MarkFileUpdated(path, file.Content);
        return result;
    }

    private static void LogToolCall(string toolName, string args)
    {
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine($"\n[tool-call] {toolName}({args})");
        Console.ResetColor();
    }

    private static void LogToolResult(string toolName, string result)
    {
        Console.ForegroundColor = ConsoleColor.DarkGreen;
        Console.WriteLine($"[tool-result] {toolName}: {result}");
        Console.ResetColor();
    }

    private static string ClampForLog(string value, int maxChars)
    {
        var oneLine = value.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= maxChars ? oneLine : oneLine[..maxChars] + "...";
    }

    private static string BuildFileContentResult(string path, string content, string operation)
    {
        return $"{operation} {path}.\n\nCurrent content:\n{content}";
    }
}

internal sealed class MemorySessionContext
{
    private const string ProfilePath = "profile.md";
    private const string LongTermPath = "long_term_memory.md";
    private const string ProjectsPrefix = "projects/";

    private readonly MemNetClient _client;
    private readonly AgentMemory _memory;
    private readonly MemNetScope _scope;
    private string? _profileContent;
    private string? _longTermContent;
    private List<string> _projectFiles = new();
    private bool _includeSnapshotOnNextTurn = true;

    public MemorySessionContext(MemNetClient client, AgentMemory memory, MemNetScope scope)
    {
        _client = client;
        _memory = memory;
        _scope = scope;
    }

    public async Task PrimeAsync(CancellationToken cancellationToken = default)
    {
        var assembled = await _client.AssembleContextAsync(
            _scope,
            new AssembleContextRequest(
                Files:
                [
                    new AssembleFileRef(ProfilePath),
                    new AssembleFileRef(LongTermPath)
                ],
                MaxDocs: 4,
                MaxCharsTotal: 40_000),
            cancellationToken);

        _profileContent = FindAssembledFileText(assembled, ProfilePath);
        _longTermContent = FindAssembledFileText(assembled, LongTermPath);

        var projects = await _memory.MemoryListFilesAsync(_scope, ProjectsPrefix, 200, cancellationToken);
        _projectFiles = projects
            .Select(x => x.Path)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _includeSnapshotOnNextTurn = true;
    }

    public IReadOnlyList<ChatMessage> BuildTurnMessages(string userInput)
    {
        if (!_includeSnapshotOnNextTurn)
        {
            return [new ChatMessage(ChatRole.User, userInput)];
        }

        _includeSnapshotOnNextTurn = false;
        return
        [
            new ChatMessage(
                ChatRole.System,
                "Use this preloaded memory snapshot as default context. " +
                "Avoid redundant memory_load_file calls for these files unless explicitly asked to verify.\n\n" +
                BuildSnapshotText()),
            new ChatMessage(ChatRole.User, userInput)
        ];
    }

    public void MarkFileUpdated(string path, string content)
    {
        var normalizedPath = NormalizePath(path);
        if (normalizedPath.Equals(ProfilePath, StringComparison.OrdinalIgnoreCase))
        {
            _profileContent = content;
            _includeSnapshotOnNextTurn = true;
            return;
        }

        if (normalizedPath.Equals(LongTermPath, StringComparison.OrdinalIgnoreCase))
        {
            _longTermContent = content;
            _includeSnapshotOnNextTurn = true;
            return;
        }

        if (normalizedPath.StartsWith(ProjectsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            AddProjectFile(normalizedPath);
            _includeSnapshotOnNextTurn = true;
        }
    }

    public void UpdateProjectCatalog(IReadOnlyList<MemoryFileListItem> files)
    {
        _projectFiles = files
            .Select(x => NormalizePath(x.Path))
            .Where(x => x.StartsWith(ProjectsPrefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _includeSnapshotOnNextTurn = true;
    }

    private static string? FindAssembledFileText(AssembleContextResponse assembled, string path)
    {
        var match = assembled.Files.FirstOrDefault(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return null;
        }

        var text = match.Document.Content["text"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        return JsonSerializer.Serialize(match.Document.Content, new JsonSerializerOptions { WriteIndented = true });
    }

    private string BuildSnapshotText()
    {
        var sb = new StringBuilder();
        AppendSnapshotSection(sb, ProfilePath, _profileContent);
        sb.AppendLine();
        AppendSnapshotSection(sb, LongTermPath, _longTermContent);
        sb.AppendLine();
        AppendProjectCatalogSection(sb, _projectFiles);
        return sb.ToString().TrimEnd();
    }

    private static void AppendSnapshotSection(StringBuilder sb, string path, string? content)
    {
        sb.AppendLine($"[{path}]");
        if (content is null)
        {
            sb.AppendLine("(not found)");
        }
        else if (string.IsNullOrWhiteSpace(content))
        {
            sb.AppendLine("(empty)");
        }
        else
        {
            sb.AppendLine(content.TrimEnd());
        }
    }

    private static void AppendProjectCatalogSection(StringBuilder sb, IReadOnlyList<string> projectFiles)
    {
        sb.AppendLine("[projects/catalog]");
        if (projectFiles.Count == 0)
        {
            sb.AppendLine("(none)");
            return;
        }

        foreach (var projectPath in projectFiles)
        {
            sb.Append("- ").AppendLine(projectPath);
        }
    }

    private void AddProjectFile(string path)
    {
        if (_projectFiles.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        _projectFiles.Add(path);
        _projectFiles = _projectFiles
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').Trim().TrimStart('/');
    }
}
