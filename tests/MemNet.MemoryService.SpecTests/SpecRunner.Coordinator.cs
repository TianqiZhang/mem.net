using System.Text.Json.Nodes;
using MemNet.MemoryService.Core;

internal sealed partial class SpecRunner
{
    private static async Task PatchDocumentHappyPathAsync()
    {
        using var scope = TestScope.Create();
        var key = scope.Keys.LongTermMemory;
        var seeded = await scope.DocumentStore.GetAsync(key) ?? throw new Exception("Seeded long_term_memory missing.");

        var result = await scope.Coordinator.PatchDocumentAsync(
            key,
            new PatchDocumentRequest(
                Ops:
                [
                    new PatchOperation("replace", "/content/preferences/0", JsonValue.Create("Use concise answers with examples."))
                ],
                Reason: "live_update",
                Evidence: new JsonObject
                {
                    ["source"] = "chat",
                    ["conversation_id"] = "conv1",
                    ["message_ids"] = new JsonArray("m1")
                }),
            ifMatch: seeded.ETag,
            actor: "spec-tests");

        Assert.True(result.ETag != seeded.ETag, "ETag should change after patch.");
        Assert.Equal("Use concise answers with examples.", result.Document.Content["preferences"]?[0]?.GetValue<string>());
        Assert.Equal("spec-tests", result.Document.UpdatedBy);
    }

    private static async Task PatchDocumentReturns412OnEtagMismatchAsync()
    {
        using var scope = TestScope.Create();
        var key = scope.Keys.LongTermMemory;

        await Assert.ThrowsAsync<ApiException>(
            async () =>
            {
                await scope.Coordinator.PatchDocumentAsync(
                    key,
                    new PatchDocumentRequest(
                        Ops:
                        [
                            new PatchOperation("replace", "/content/preferences/0", JsonValue.Create("Mismatch test"))
                        ],
                        Reason: "live_update",
                        Evidence: null),
                    ifMatch: "\"stale\"",
                    actor: "spec-tests");
            },
            ex => ex.StatusCode == 412 && ex.Code == "ETAG_MISMATCH");
    }

    private static async Task PatchDocumentReturns422OnInvalidPatchPathAsync()
    {
        using var scope = TestScope.Create();
        var key = scope.Keys.LongTermMemory;
        var seeded = await scope.DocumentStore.GetAsync(key) ?? throw new Exception("Seeded long_term_memory missing.");

        await Assert.ThrowsAsync<ApiException>(
            async () =>
            {
                await scope.Coordinator.PatchDocumentAsync(
                    key,
                    new PatchDocumentRequest(
                        Ops:
                        [
                            new PatchOperation("replace", "/content/profile/display_name", JsonValue.Create("Oops"))
                        ],
                        Reason: "live_update",
                        Evidence: null),
                    ifMatch: seeded.ETag,
                    actor: "spec-tests");
            },
            ex => ex.StatusCode == 422 && ex.Code == "INVALID_PATCH_PATH");
    }

    private static async Task PatchFileTextEditsApplyDeterministicallyAsync()
    {
        using var scope = TestScope.Create();
        var now = DateTimeOffset.UtcNow;
        var key = new DocumentKey(scope.Keys.Tenant, scope.Keys.User, "user/text_patch.md");
        var seeded = await scope.DocumentStore.UpsertAsync(
            key,
            new DocumentEnvelope(
                DocId: Guid.NewGuid().ToString("N"),
                SchemaId: "memnet.file",
                SchemaVersion: "1.0.0",
                CreatedAt: now,
                UpdatedAt: now,
                UpdatedBy: "seed",
                Content: new JsonObject
                {
                    ["content_type"] = "text/markdown",
                    ["text"] = "line A\nline A\nline B\n"
                }),
            ifMatch: "*");

        var patched = await scope.Coordinator.PatchDocumentAsync(
            key,
            new PatchDocumentRequest(
                Ops: [],
                Reason: "text_patch",
                Evidence: null,
                Edits:
                [
                    new TextPatchEdit("line A\n", "line X\n", 2)
                ]),
            ifMatch: seeded.ETag,
            actor: "spec-tests");

        Assert.Equal("line A\nline X\nline B\n", patched.Document.Content["text"]?.GetValue<string>());
    }

    private static async Task PatchFileTextEditsRejectAmbiguousMatchAsync()
    {
        using var scope = TestScope.Create();
        var now = DateTimeOffset.UtcNow;
        var key = new DocumentKey(scope.Keys.Tenant, scope.Keys.User, "user/text_patch_ambiguous.md");
        var seeded = await scope.DocumentStore.UpsertAsync(
            key,
            new DocumentEnvelope(
                DocId: Guid.NewGuid().ToString("N"),
                SchemaId: "memnet.file",
                SchemaVersion: "1.0.0",
                CreatedAt: now,
                UpdatedAt: now,
                UpdatedBy: "seed",
                Content: new JsonObject
                {
                    ["content_type"] = "text/markdown",
                    ["text"] = "alpha\nalpha\n"
                }),
            ifMatch: "*");

        await Assert.ThrowsAsync<ApiException>(
            async () =>
            {
                await scope.Coordinator.PatchDocumentAsync(
                    key,
                    new PatchDocumentRequest(
                        Ops: [],
                        Reason: "text_patch",
                        Evidence: null,
                        Edits:
                        [
                            new TextPatchEdit("alpha\n", "beta\n", null)
                        ]),
                    ifMatch: seeded.ETag,
                    actor: "spec-tests");
            },
            ex => ex.StatusCode == 422 && ex.Code == "PATCH_MATCH_AMBIGUOUS");
    }

    private static async Task PatchFileTextEditsRejectMissingMatchAsync()
    {
        using var scope = TestScope.Create();
        var now = DateTimeOffset.UtcNow;
        var key = new DocumentKey(scope.Keys.Tenant, scope.Keys.User, "user/text_patch_missing.md");
        var seeded = await scope.DocumentStore.UpsertAsync(
            key,
            new DocumentEnvelope(
                DocId: Guid.NewGuid().ToString("N"),
                SchemaId: "memnet.file",
                SchemaVersion: "1.0.0",
                CreatedAt: now,
                UpdatedAt: now,
                UpdatedBy: "seed",
                Content: new JsonObject
                {
                    ["content_type"] = "text/markdown",
                    ["text"] = "alpha\n"
                }),
            ifMatch: "*");

        await Assert.ThrowsAsync<ApiException>(
            async () =>
            {
                await scope.Coordinator.PatchDocumentAsync(
                    key,
                    new PatchDocumentRequest(
                        Ops: [],
                        Reason: "text_patch",
                        Evidence: null,
                        Edits:
                        [
                            new TextPatchEdit("missing\n", "beta\n", null)
                        ]),
                    ifMatch: seeded.ETag,
                    actor: "spec-tests");
            },
            ex => ex.StatusCode == 422 && ex.Code == "PATCH_MATCH_NOT_FOUND");
    }

    private static async Task AssembleContextIncludesRequestedFilesAndRespectsBudgetsAsync()
    {
        using var scope = TestScope.Create();

        var full = await scope.Coordinator.AssembleContextAsync(
            tenantId: scope.Keys.Tenant,
            userId: scope.Keys.User,
            request: new AssembleContextRequest(
                Files:
                [
                    new AssembleFileRef("user/profile.json"),
                    new AssembleFileRef("user/long_term_memory.json")
                ],
                MaxDocs: 5,
                MaxCharsTotal: 30000));

        Assert.Equal(2, full.Files.Count);

        var tinyBudget = await scope.Coordinator.AssembleContextAsync(
            tenantId: scope.Keys.Tenant,
            userId: scope.Keys.User,
            request: new AssembleContextRequest(
                Files:
                [
                    new AssembleFileRef("user/profile.json"),
                    new AssembleFileRef("user/long_term_memory.json")
                ],
                MaxDocs: 5,
                MaxCharsTotal: 300));

        Assert.True(tinyBudget.DroppedFiles.Count > 0, "Expected dropped_files when char budget is small.");
    }

    private static async Task AssembleContextRejectsEmptyRequestAsync()
    {
        using var scope = TestScope.Create();

        await Assert.ThrowsAsync<ApiException>(
            async () =>
            {
                await scope.Coordinator.AssembleContextAsync(
                    tenantId: scope.Keys.Tenant,
                    userId: scope.Keys.User,
                    request: new AssembleContextRequest(
                        Files: [],
                        MaxDocs: 5,
                        MaxCharsTotal: 30000));
            },
            ex => ex.StatusCode == 400 && ex.Code == "MISSING_ASSEMBLY_TARGETS");
    }

    private static async Task EventSearchReturnsRelevantResultsAsync()
    {
        using var scope = TestScope.Create();

        await scope.Coordinator.WriteEventAsync(new EventDigest(
            EventId: "evt1",
            TenantId: scope.Keys.Tenant,
            UserId: scope.Keys.User,
            ServiceId: "assistant-a",
            Timestamp: DateTimeOffset.UtcNow,
            SourceType: "chat",
            Digest: "Investigated retrieval latency in project alpha.",
            Keywords: ["retrieval", "latency"],
            ProjectIds: ["project-alpha"],
            Evidence: new JsonObject
            {
                ["source"] = "chat",
                ["message_ids"] = new JsonArray("m1"),
                ["snapshot_uri"] = "blob://snapshots/1"
            }));

        await scope.Coordinator.WriteEventAsync(new EventDigest(
            EventId: "evt2",
            TenantId: scope.Keys.Tenant,
            UserId: scope.Keys.User,
            ServiceId: "assistant-a",
            Timestamp: DateTimeOffset.UtcNow,
            SourceType: "chat",
            Digest: "General preferences update.",
            Keywords: ["preferences"],
            ProjectIds: ["project-beta"],
            Evidence: new JsonObject
            {
                ["source"] = "chat",
                ["message_ids"] = new JsonArray("m2"),
                ["snapshot_uri"] = "blob://snapshots/2"
            }));

        var search = await scope.Coordinator.SearchEventsAsync(
            scope.Keys.Tenant,
            scope.Keys.User,
            new SearchEventsRequest("latency", "assistant-a", "chat", "project-alpha", null, null, 3));

        Assert.True(search.Results.Count == 1, "Expected one filtered event result.");
        Assert.Equal("evt1", search.Results[0].EventId);
    }
}
