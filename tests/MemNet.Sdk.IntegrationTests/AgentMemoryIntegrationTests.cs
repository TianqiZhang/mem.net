using System.Text.Json.Nodes;
using MemNet.AgentMemory;
using MemNet.Client;

namespace MemNet.Sdk.IntegrationTests;

[Collection(MemNetApiTestCollection.Name)]
public sealed class AgentMemoryIntegrationTests(MemNetApiFixture fixture)
{
    [Fact]
    public async Task PrepareTurnFlow_WorksWithRememberedEvents()
    {
        fixture.ResetDataRoot();
        using var client = SdkTestData.CreateClient(fixture.Client, serviceId: "agent-sdk-tests");

        var scope = new MemNetScope(fixture.TenantId, fixture.UserId);
        await SdkTestData.SeedDefaultUserFilesAsync(client, scope);

        var policy = new AgentMemoryPolicy(
            PolicyId: "default",
            Slots:
            [
                new MemorySlotPolicy(
                    SlotId: "profile",
                    Path: "user/profile.json",
                    PathTemplate: null,
                    LoadByDefault: true),
                new MemorySlotPolicy(
                    SlotId: "long_term_memory",
                    Path: "user/long_term_memory.json",
                    PathTemplate: null,
                    LoadByDefault: true)
            ]);

        var memory = new MemNet.AgentMemory.AgentMemory(client, policy);
        await memory.RememberAsync(
            scope,
            new RememberRequest(
                new EventDigest(
                    EventId: "evt-agent-sdk-1",
                    TenantId: fixture.TenantId,
                    UserId: fixture.UserId,
                    ServiceId: "agent-sdk-tests",
                    Timestamp: DateTimeOffset.UtcNow,
                    SourceType: "chat",
                    Digest: "prepare-turn event",
                    Keywords: ["agent", "memory"],
                    ProjectIds: ["project-a"],
                    Evidence: new JsonObject
                    {
                        ["source"] = "agent-sdk-tests",
                        ["message_ids"] = new JsonArray("m-agent-sdk")
                    })));

        var prepared = await memory.PrepareTurnAsync(
            scope,
            new PrepareTurnRequest(
                RecallQuery: "prepare-turn",
                RecallTopK: 5));

        Assert.Equal(2, prepared.Documents.Count);
        Assert.Contains(prepared.Events, x => x.EventId == "evt-agent-sdk-1");
    }

    [Fact]
    public async Task FileToolFlow_WritePatchLoad_Works()
    {
        fixture.ResetDataRoot();
        using var client = SdkTestData.CreateClient(fixture.Client, serviceId: "agent-sdk-tests");

        var scope = new MemNetScope(fixture.TenantId, fixture.UserId);
        var memory = new MemNet.AgentMemory.AgentMemory(
            client,
            new AgentMemoryPolicy(
                PolicyId: "default",
                Slots: []));

        const string path = "user/notes.md";
        await memory.MemoryWriteFileAsync(
            scope,
            path,
            "## Notes\n- line one\n");

        await memory.MemoryPatchFileAsync(
            scope,
            path,
            [
                new MemoryPatchEdit(
                    OldText: "## Notes\n- line one\n",
                    NewText: "## Notes\n- line one\n- line two\n")
            ]);

        var loaded = await memory.MemoryLoadFileAsync(scope, path);
        Assert.Contains("line two", loaded.Content);
    }
}
