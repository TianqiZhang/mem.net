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
            PolicyId: "memory-agent-default",
            Slots:
            [
                new MemNet.AgentMemory.MemorySlotPolicy(
                    SlotId: "profile",
                    Path: "user/profile.json",
                    PathTemplate: null,
                    LoadByDefault: true,
                    PatchRules: new MemNet.AgentMemory.SlotPatchRules(
                        AllowedPaths: ["/profile", "/projects_index"],
                        RequiredContentPaths: ["/profile"])),
                new MemNet.AgentMemory.MemorySlotPolicy(
                    SlotId: "long_term_memory",
                    Path: "user/long_term_memory.json",
                    PathTemplate: null,
                    LoadByDefault: true,
                    PatchRules: new MemNet.AgentMemory.SlotPatchRules(
                        AllowedPaths: ["/preferences", "/durable_facts", "/pending_confirmations"])),
                new MemNet.AgentMemory.MemorySlotPolicy(
                    SlotId: "project",
                    Path: null,
                    PathTemplate: "projects/{project_id}.json",
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
                    Evidence: new JsonObject
                    {
                        ["source"] = "agent-sdk-tests",
                        ["message_ids"] = new JsonArray("m-agent-sdk"),
                        ["snapshot_uri"] = "blob://snapshots/agent-sdk"
                    })));

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
            PolicyId: "memory-agent-default",
            Slots:
            [
                new MemNet.AgentMemory.MemorySlotPolicy(
                    SlotId: "long_term_memory",
                    Path: "user/long_term_memory.json",
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
        var fileRef = new MemNet.Client.FileRef("user/long_term_memory.json");
        var injectedConflict = false;

        var updated = await client.UpdateWithRetryAsync(
            memScope,
            fileRef,
            current =>
            {
                if (!injectedConflict)
                {
                    injectedConflict = true;
                    client.PatchFileAsync(
                        memScope,
                        fileRef,
                        new MemNet.Client.PatchDocumentRequest(
                            Ops:
                            [
                                new MemNet.Client.PatchOperation("replace", "/content/preferences/0", JsonValue.Create("intermediate"))
                            ],
                            Reason: "inject_conflict"),
                        current.ETag).GetAwaiter().GetResult();
                }

                return MemNet.Client.FileUpdate.FromPatch(
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

    private static async Task AgentMemoryFileToolFlowWorksAsync()
    {
        using var scope = TestScope.Create();
        using var host = await ServiceHost.StartAsync(scope.RepoRoot, scope.DataRoot);
        using var client = new MemNet.Client.MemNetClient(new MemNet.Client.MemNetClientOptions
        {
            BaseAddress = host.BaseAddress,
            ServiceId = "agent-sdk-tests"
        });

        var policy = new MemNet.AgentMemory.AgentMemoryPolicy(
            PolicyId: "memory-agent-default",
            Slots: []);

        var agentMemory = new MemNet.AgentMemory.AgentMemory(client, policy);
        var memScope = new MemNet.Client.MemNetScope(scope.Keys.Tenant, scope.Keys.User);
        var path = "user/notes.md";

        await agentMemory.MemoryWriteFileAsync(
            memScope,
            path,
            "## Notes\n- line one\n");

        await agentMemory.MemoryPatchFileAsync(
            memScope,
            path,
            [
                new MemNet.AgentMemory.MemoryPatchEdit(
                    OldText: "## Notes\n- line one\n",
                    NewText: "## Notes\n- line one\n- line two\n")
            ]);

        var loaded = await agentMemory.MemoryLoadFileAsync(memScope, path);
        Assert.True(loaded.Content.Contains("line two", StringComparison.Ordinal), "Expected patched markdown content.");
    }

    private static async Task SdkUpdateWithRetryResolvesEtagConflictsForTextPatchFlowAsync()
    {
        using var scope = TestScope.Create();
        using var host = await ServiceHost.StartAsync(scope.RepoRoot, scope.DataRoot);
        using var client = new MemNet.Client.MemNetClient(new MemNet.Client.MemNetClientOptions
        {
            BaseAddress = host.BaseAddress,
            ServiceId = "agent-sdk-tests"
        });

        var memScope = new MemNet.Client.MemNetScope(scope.Keys.Tenant, scope.Keys.User);
        var fileRef = new MemNet.Client.FileRef("user/retry-text.md");
        var now = DateTimeOffset.UtcNow;

        var seededEnvelope = new MemNet.Client.DocumentEnvelope(
            DocId: Guid.NewGuid().ToString("N"),
            SchemaId: "memnet.file",
            SchemaVersion: "1.0.0",
            CreatedAt: now,
            UpdatedAt: now,
            UpdatedBy: "seed",
            Content: new JsonObject
            {
                ["content_type"] = "text/markdown",
                ["text"] = "start\n"
            });

        await client.WriteFileAsync(
            memScope,
            fileRef,
            new MemNet.Client.ReplaceDocumentRequest(seededEnvelope, "seed"),
            ifMatch: "*");

        var injectedConflict = false;
        var updated = await client.UpdateWithRetryAsync(
            memScope,
            fileRef,
            current =>
            {
                if (!injectedConflict)
                {
                    injectedConflict = true;
                    var conflictEnvelope = current.Document with
                    {
                        Content = new JsonObject
                        {
                            ["content_type"] = "text/markdown",
                            ["text"] = "middle\n"
                        }
                    };

                    client.WriteFileAsync(
                        memScope,
                        fileRef,
                        new MemNet.Client.ReplaceDocumentRequest(conflictEnvelope, "inject_conflict"),
                        current.ETag).GetAwaiter().GetResult();
                }

                return MemNet.Client.FileUpdate.FromPatch(
                    new MemNet.Client.PatchDocumentRequest(
                        Ops: [],
                        Reason: "retry_text_patch",
                        Evidence: null,
                        Edits:
                        [
                            new MemNet.Client.TextPatchEdit("middle\n", "final\n")
                        ]));
            },
            maxConflictRetries: 3);

        Assert.True(injectedConflict, "Expected conflict injection to execute.");
        Assert.Equal("final\n", updated.Document.Content["text"]?.GetValue<string>());
    }

    private static async Task SdkUpdateWithRetryResolvesEtagConflictsForWriteFlowAsync()
    {
        using var scope = TestScope.Create();
        using var host = await ServiceHost.StartAsync(scope.RepoRoot, scope.DataRoot);
        using var client = new MemNet.Client.MemNetClient(new MemNet.Client.MemNetClientOptions
        {
            BaseAddress = host.BaseAddress,
            ServiceId = "agent-sdk-tests"
        });

        var memScope = new MemNet.Client.MemNetScope(scope.Keys.Tenant, scope.Keys.User);
        var fileRef = new MemNet.Client.FileRef("user/retry-write.md");
        var now = DateTimeOffset.UtcNow;

        var seededEnvelope = new MemNet.Client.DocumentEnvelope(
            DocId: Guid.NewGuid().ToString("N"),
            SchemaId: "memnet.file",
            SchemaVersion: "1.0.0",
            CreatedAt: now,
            UpdatedAt: now,
            UpdatedBy: "seed",
            Content: new JsonObject
            {
                ["content_type"] = "text/markdown",
                ["text"] = "v0\n"
            });

        await client.WriteFileAsync(
            memScope,
            fileRef,
            new MemNet.Client.ReplaceDocumentRequest(seededEnvelope, "seed"),
            ifMatch: "*");

        var injectedConflict = false;
        var updated = await client.UpdateWithRetryAsync(
            memScope,
            fileRef,
            current =>
            {
                if (!injectedConflict)
                {
                    injectedConflict = true;
                    var conflictEnvelope = current.Document with
                    {
                        Content = new JsonObject
                        {
                            ["content_type"] = "text/markdown",
                            ["text"] = "v1\n"
                        }
                    };

                    client.WriteFileAsync(
                        memScope,
                        fileRef,
                        new MemNet.Client.ReplaceDocumentRequest(conflictEnvelope, "inject_conflict"),
                        current.ETag).GetAwaiter().GetResult();
                }

                var target = current.Document with
                {
                    Content = new JsonObject
                    {
                        ["content_type"] = "text/markdown",
                        ["text"] = "v-final\n"
                    }
                };

                return MemNet.Client.FileUpdate.FromWrite(
                    new MemNet.Client.ReplaceDocumentRequest(target, "retry_write"));
            },
            maxConflictRetries: 3);

        Assert.True(injectedConflict, "Expected conflict injection to execute.");
        Assert.Equal("v-final\n", updated.Document.Content["text"]?.GetValue<string>());
    }
}
