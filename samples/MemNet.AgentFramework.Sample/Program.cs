using System.Text;
using MemNet.AgentMemory;
using MemNet.Client;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

var config = SampleConfig.LoadFromEnvironment();

using var memClient = new MemNetClient(new MemNetClientOptions
{
    BaseAddress = new Uri(config.MemNetBaseUrl),
    ServiceId = config.ServiceId
});

var scope = new MemNetScope(config.TenantId, config.UserId);
var memory = new AgentMemory(memClient, new AgentMemoryPolicy("default", Array.Empty<MemorySlotPolicy>()));
var memorySessionContext = new MemorySessionContext(memClient, memory, scope);
await memorySessionContext.PrimeAsync();
var memoryTools = new MemoryTools(memory, scope, memorySessionContext);
var chatClient = LlmClientFactory.Create(config);

AIAgent agent = chatClient.AsAIAgent(
        name: "memory-agent",
        instructions: AgentPrompt.Instructions,
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
Console.WriteLine($"LLM provider: {config.ProviderLabel}; model/deployment: {config.ModelName}");
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

    await TurnDigestWriter.WriteAsync(memory, scope, config.ServiceId, input, responseText.ToString());
}

return;
