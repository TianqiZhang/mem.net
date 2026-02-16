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
                PolicyId: "project-copilot-v1",
                BindingId: "long_term_memory",
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
                        PolicyId: "project-copilot-v1",
                        BindingId: "long_term_memory",
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

    private static async Task PatchDocumentReturns422OnPathPolicyViolationAsync()
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
                        PolicyId: "project-copilot-v1",
                        BindingId: "long_term_memory",
                        Ops:
                        [
                            new PatchOperation("replace", "/content/profile/display_name", JsonValue.Create("Oops"))
                        ],
                        Reason: "live_update",
                        Evidence: null),
                    ifMatch: seeded.ETag,
                    actor: "spec-tests");
            },
            ex => ex.StatusCode == 422 && ex.Code == "PATH_NOT_WRITABLE");
    }

    private static async Task PatchDocumentV2AllowsPolicyFreeMutationAsync()
    {
        using var scope = TestScope.Create();
        var key = scope.Keys.LongTermMemory;
        var seeded = await scope.DocumentStore.GetAsync(key) ?? throw new Exception("Seeded long_term_memory missing.");

        var result = await scope.Coordinator.PatchDocumentAsync(
            key,
            new PatchDocumentRequest(
                PolicyId: null,
                BindingId: null,
                Ops:
                [
                    new PatchOperation("add", "/content/freeform_note", JsonValue.Create("V2 policy-free update"))
                ],
                Reason: "live_update",
                Evidence: null),
            ifMatch: seeded.ETag,
            actor: "spec-tests");

        Assert.Equal("V2 policy-free update", result.Document.Content["freeform_note"]?.GetValue<string>());
    }

    private static async Task PatchDocumentRejectsPartialSelectorModeAsync()
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
                        PolicyId: "project-copilot-v1",
                        BindingId: null,
                        Ops:
                        [
                            new PatchOperation("replace", "/content/preferences/0", JsonValue.Create("bad selector mode"))
                        ],
                        Reason: "live_update",
                        Evidence: null),
                    ifMatch: seeded.ETag,
                    actor: "spec-tests");
            },
            ex => ex.StatusCode == 400 && ex.Code == "INVALID_SELECTOR_MODE");
    }

    private static async Task AssembleContextIncludesDefaultDocsAndRespectsBudgetsAsync()
    {
        using var scope = TestScope.Create();

        var full = await scope.Coordinator.AssembleContextAsync(
            tenantId: scope.Keys.Tenant,
            userId: scope.Keys.User,
            request: new AssembleContextRequest(
                PolicyId: "project-copilot-v1",
                Documents: null,
                MaxDocs: 5,
                MaxCharsTotal: 30000));

        Assert.Equal(2, full.Documents.Count);
        Assert.True(!full.Documents.Any(x => x.BindingId == "project_memory"), "Project document should not be included by default.");

        var tinyBudget = await scope.Coordinator.AssembleContextAsync(
            tenantId: scope.Keys.Tenant,
            userId: scope.Keys.User,
            request: new AssembleContextRequest(
                PolicyId: "project-copilot-v1",
                Documents: null,
                MaxDocs: 5,
                MaxCharsTotal: 300));

        Assert.True(tinyBudget.DroppedBindings.Count > 0, "Expected dropped_bindings when char budget is small.");
    }

    private static async Task AssembleContextWithExplicitDocumentsV2Async()
    {
        using var scope = TestScope.Create();

        var assembled = await scope.Coordinator.AssembleContextAsync(
            tenantId: scope.Keys.Tenant,
            userId: scope.Keys.User,
            request: new AssembleContextRequest(
                PolicyId: null,
                Documents:
                [
                    new AssembleDocumentRef("user", "profile.json"),
                    new AssembleDocumentRef("user", "long_term_memory.json")
                ],
                MaxDocs: 5,
                MaxCharsTotal: 30000));

        Assert.Equal(2, assembled.Documents.Count);
        Assert.True(assembled.Documents.All(x => x.BindingId is null), "V2 explicit assembly should not emit binding IDs.");
        Assert.True(assembled.DroppedBindings.Count == 0, "V2 explicit assembly should not use dropped_bindings.");
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
