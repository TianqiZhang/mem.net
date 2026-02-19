using System.ComponentModel;
using System.ClientModel;
using System.Text;
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
var memoryTools = new MemoryTools(memory, scope);
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
            "Prefer: user/profile.md, user/long_term_memory.md, projects/{project_id}.md.",
        tools:
        [
            AIFunctionFactory.Create(memoryTools.MemoryRecallAsync, name: "memory_recall"),
            AIFunctionFactory.Create(memoryTools.MemoryLoadFileAsync, name: "memory_load_file"),
            AIFunctionFactory.Create(memoryTools.MemoryPatchFileAsync, name: "memory_patch_file"),
            AIFunctionFactory.Create(memoryTools.MemoryWriteFileAsync, name: "memory_write_file")
        ]);

var health = await memClient.GetServiceStatusAsync();
Console.WriteLine($"Connected to mem.net: {health.Service} ({health.Status})");
Console.WriteLine($"LLM provider: {providerLabel}; model/deployment: {modelName}");
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
    await foreach (var update in agent.RunStreamingAsync(input, session))
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

    public MemoryTools(AgentMemory memory, MemNetScope scope)
    {
        _memory = memory;
        _scope = scope;
    }

    [Description("Search event digests in long-term memory and return top results.")]
    public async Task<string> MemoryRecallAsync(
        [Description("Natural-language query for event recall.")] string query,
        [Description("Maximum number of hits. Recommended range: 1-20.")] int topK = 8,
        CancellationToken cancellationToken = default)
    {
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

    [Description("Load a memory file by path and return its full text content.")]
    public async Task<string> MemoryLoadFileAsync(
        [Description("File path, for example user/profile.md.")] string path,
        CancellationToken cancellationToken = default)
    {
        LogToolCall("memory_load_file", $"path=\"{path}\"");
        try
        {
            var file = await _memory.MemoryLoadFileAsync(_scope, path, cancellationToken);
            LogToolResult("memory_load_file", $"ok (chars={file.Content.Length}, etag={file.ETag})");
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
        [Description("File path, for example user/long_term_memory.md.")] string path,
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

            LogToolResult("memory_patch_file", $"ok (etag={file.ETag})");
            return $"Patched {path}. New etag: {file.ETag}";
        }
        catch (MemNetApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            LogToolResult("memory_patch_file", "not_found");
            return $"File not found: {path}";
        }
    }

    [Description("Write full text content to a memory file (create or replace).")]
    public async Task<string> MemoryWriteFileAsync(
        [Description("File path, for example user/long_term_memory.md.")] string path,
        [Description("Full markdown content to write.")] string content,
        CancellationToken cancellationToken = default)
    {
        LogToolCall("memory_write_file", $"path=\"{path}\", chars={content.Length}");
        var file = await _memory.MemoryWriteFileAsync(_scope, path, content, cancellationToken: cancellationToken);
        LogToolResult("memory_write_file", $"ok (etag={file.ETag})");
        return $"Wrote {path}. New etag: {file.ETag}";
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
}
