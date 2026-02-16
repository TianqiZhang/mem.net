using System.Text.Json.Nodes;
using MemNet.Client;

internal sealed partial class SpecRunner
{
    private static async Task AgentMemoryPrepareTurnFlowWorksAsync()
    {
        using var scope = TestScope.Create();
        using var host = await ServiceHost.StartAsync(scope.RepoRoot, scope.DataRoot);
        using var client = new MemNet.Client.MemNetClient(new MemNet.Client.MemNetClientOptions
        {
            BaseAddress = host.BaseAddress,
            ServiceId = "agent-sdk-tests"
        });

        var policy = new MemNet.AgentMemory.AgentMemoryPolicy(
            PolicyId: "learn-companion-default",
            Slots:
            [
                new MemNet.AgentMemory.MemorySlotPolicy(
                    SlotId: "profile",
                    Namespace: "user",
                    Path: "profile.json",
                    PathTemplate: null,
                    LoadByDefault: true,
                    PatchRules: new MemNet.AgentMemory.SlotPatchRules(
                        AllowedPaths: ["/profile", "/projects_index"],
                        RequiredContentPaths: ["/profile"])),
                new MemNet.AgentMemory.MemorySlotPolicy(
                    SlotId: "long_term_memory",
                    Namespace: "user",
                    Path: "long_term_memory.json",
                    PathTemplate: null,
                    LoadByDefault: true,
                    PatchRules: new MemNet.AgentMemory.SlotPatchRules(
                        AllowedPaths: ["/preferences", "/durable_facts", "/pending_confirmations"])),
                new MemNet.AgentMemory.MemorySlotPolicy(
                    SlotId: "project",
                    Namespace: "projects",
                    Path: null,
                    PathTemplate: "{project_id}.json",
                    LoadByDefault: false)
            ]);

        var agentMemory = new MemNet.AgentMemory.AgentMemory(client, policy);
        var memScope = new MemNet.Client.MemNetScope(scope.Keys.Tenant, scope.Keys.User);

        await agentMemory.RememberAsync(
            memScope,
            new MemNet.AgentMemory.RememberRequest(
                new MemNet.Client.EventDigest(
                    EventId: "evt-agent-sdk-1",
                    TenantId: scope.Keys.Tenant,
                    UserId: scope.Keys.User,
                    ServiceId: "agent-sdk-tests",
                    Timestamp: DateTimeOffset.UtcNow,
                    SourceType: "chat",
                    Digest: "Agent memory prepare-turn event",
                    Keywords: ["agent", "memory"],
                    ProjectIds: ["project-alpha"],
                    SnapshotUri: "blob://snapshots/agent-sdk",
                    Evidence: new MemNet.Client.EventEvidence(["m-agent-sdk"], 1, 1))));

        var prepared = await agentMemory.PrepareTurnAsync(
            memScope,
            new MemNet.AgentMemory.PrepareTurnRequest(
                RecallQuery: "prepare-turn event",
                RecallTopK: 5));

        Assert.Equal(2, prepared.Documents.Count);
        Assert.True(prepared.Events.Any(x => x.EventId == "evt-agent-sdk-1"), "Expected remembered event in recall results.");
    }

    private static async Task AgentMemoryPatchSlotRulesAreEnforcedClientSideAsync()
    {
        using var scope = TestScope.Create();
        using var host = await ServiceHost.StartAsync(scope.RepoRoot, scope.DataRoot);
        using var client = new MemNet.Client.MemNetClient(new MemNet.Client.MemNetClientOptions
        {
            BaseAddress = host.BaseAddress,
            ServiceId = "agent-sdk-tests"
        });

        var policy = new MemNet.AgentMemory.AgentMemoryPolicy(
            PolicyId: "learn-companion-default",
            Slots:
            [
                new MemNet.AgentMemory.MemorySlotPolicy(
                    SlotId: "long_term_memory",
                    Namespace: "user",
                    Path: "long_term_memory.json",
                    PathTemplate: null,
                    LoadByDefault: true,
                    PatchRules: new MemNet.AgentMemory.SlotPatchRules(
                        AllowedPaths: ["/preferences", "/durable_facts", "/pending_confirmations"]))
            ]);

        var agentMemory = new MemNet.AgentMemory.AgentMemory(client, policy);
        var memScope = new MemNet.Client.MemNetScope(scope.Keys.Tenant, scope.Keys.User);

        var loaded = await agentMemory.LoadSlotAsync(memScope, "long_term_memory");

        await agentMemory.PatchSlotAsync(
            memScope,
            "long_term_memory",
            new MemNet.AgentMemory.SlotPatchRequest(
                Ops:
                [
                    new MemNet.Client.PatchOperation("replace", "/content/preferences/0", JsonValue.Create("agent sdk patch"))
                ],
                Reason: "agent_patch"),
            ifMatch: loaded.ETag);

        await Assert.ThrowsAsync<MemNet.Client.MemNetException>(
            async () =>
            {
                await agentMemory.PatchSlotAsync(
                    memScope,
                    "long_term_memory",
                    new MemNet.AgentMemory.SlotPatchRequest(
                        Ops:
                        [
                            new MemNet.Client.PatchOperation("replace", "/content/profile/display_name", JsonValue.Create("blocked"))
                        ],
                        Reason: "agent_patch"),
                    ifMatch: loaded.ETag);
            },
            ex => ex.Message.Contains("not allowed", StringComparison.Ordinal));
    }

    private static async Task SdkUpdateWithRetryResolvesEtagConflictsAsync()
    {
        using var scope = TestScope.Create();
        using var host = await ServiceHost.StartAsync(scope.RepoRoot, scope.DataRoot);
        using var client = new MemNet.Client.MemNetClient(new MemNet.Client.MemNetClientOptions
        {
            BaseAddress = host.BaseAddress,
            ServiceId = "agent-sdk-tests"
        });

        var memScope = new MemNet.Client.MemNetScope(scope.Keys.Tenant, scope.Keys.User);
        var docRef = new MemNet.Client.DocumentRef("user", "long_term_memory.json");
        var injectedConflict = false;

        var updated = await client.UpdateWithRetryAsync(
            memScope,
            docRef,
            current =>
            {
                if (!injectedConflict)
                {
                    injectedConflict = true;
                    client.PatchDocumentAsync(
                        memScope,
                        docRef,
                        new MemNet.Client.PatchDocumentRequest(
                            Ops:
                            [
                                new MemNet.Client.PatchOperation("replace", "/content/preferences/0", JsonValue.Create("intermediate"))
                            ],
                            Reason: "inject_conflict"),
                        current.ETag).GetAwaiter().GetResult();
                }

                return MemNet.Client.DocumentUpdate.FromPatch(
                    new MemNet.Client.PatchDocumentRequest(
                        Ops:
                        [
                            new MemNet.Client.PatchOperation("replace", "/content/preferences/0", JsonValue.Create("final"))
                        ],
                        Reason: "retry_update"));
            },
            maxConflictRetries: 3);

        Assert.True(injectedConflict, "Expected conflict injection to execute.");
        Assert.Equal("final", updated.Document.Content["preferences"]?[0]?.GetValue<string>());
    }
}
