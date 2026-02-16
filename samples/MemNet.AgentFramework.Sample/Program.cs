using System.ComponentModel;
using System.Text;
using MemNet.AgentMemory;
using MemNet.Client;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;

var openAiApiKey = RequireEnv("OPENAI_API_KEY");
var openAiModel = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o-mini";

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
var chatClient = new OpenAIClient(openAiApiKey)
    .GetChatClient(openAiModel)
    .AsIChatClient();

AIAgent agent = chatClient.AsAIAgent(
        name: "memory-agent",
        instructions:
            "You are a memory agent. " +
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

    var response = await agent.RunAsync(input, session);
    var responseText = response?.ToString() ?? string.Empty;

    Console.WriteLine();
    Console.WriteLine(responseText);
    Console.WriteLine();

    await WriteTurnDigestAsync(memory, scope, serviceId, input, responseText);
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
        var results = await _memory.MemoryRecallAsync(_scope, query, topK, cancellationToken);
        if (results.Count == 0)
        {
            return "No event memories matched that query.";
        }

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
        try
        {
            var file = await _memory.MemoryLoadFileAsync(_scope, path, cancellationToken);
            return file.Content;
        }
        catch (MemNetApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
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
        try
        {
            var file = await _memory.MemoryPatchFileAsync(
                _scope,
                path,
                [new MemoryPatchEdit(old_text, new_text, occurrence)],
                cancellationToken: cancellationToken);

            return $"Patched {path}. New etag: {file.ETag}";
        }
        catch (MemNetApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return $"File not found: {path}";
        }
    }

    [Description("Write full text content to a memory file (create or replace).")]
    public async Task<string> MemoryWriteFileAsync(
        [Description("File path, for example user/long_term_memory.md.")] string path,
        [Description("Full markdown content to write.")] string content,
        CancellationToken cancellationToken = default)
    {
        var file = await _memory.MemoryWriteFileAsync(_scope, path, content, cancellationToken: cancellationToken);
        return $"Wrote {path}. New etag: {file.ETag}";
    }
}
