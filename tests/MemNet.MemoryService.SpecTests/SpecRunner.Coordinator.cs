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
                Evidence: new EvidenceRef("conv1", ["m1"], null)),
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

    private static async Task AssembleContextIncludesRequestedDocsAndRespectsBudgetsAsync()
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
            SnapshotUri: "blob://snapshots/1",
            Evidence: new EventEvidence(["m1"], 1, 2)));

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
            SnapshotUri: "blob://snapshots/2",
            Evidence: new EventEvidence(["m2"], 3, 4)));

        var search = await scope.Coordinator.SearchEventsAsync(
            scope.Keys.Tenant,
            scope.Keys.User,
            new SearchEventsRequest("latency", "assistant-a", "chat", "project-alpha", null, null, 3));

        Assert.True(search.Results.Count == 1, "Expected one filtered event result.");
        Assert.Equal("evt1", search.Results[0].EventId);
    }
}
