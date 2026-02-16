using System.Net;

internal sealed partial class SpecRunner
{
    private static async Task SdkClientPatchAndGetFlowWorksAsync()
    {
        using var scope = TestScope.Create();
        using var host = await ServiceHost.StartAsync(scope.RepoRoot, scope.DataRoot);
        using var client = new MemNet.Client.MemNetClient(new MemNet.Client.MemNetClientOptions
        {
            BaseAddress = host.BaseAddress,
            ServiceId = "sdk-tests"
        });

        var memScope = new MemNet.Client.MemNetScope(scope.Keys.Tenant, scope.Keys.User);
        var docRef = new MemNet.Client.DocumentRef("user", "long_term_memory.json");

        var before = await client.GetDocumentAsync(memScope, docRef);
        var patched = await client.PatchDocumentAsync(
            memScope,
            docRef,
            new MemNet.Client.PatchDocumentRequest(
                Ops:
                [
                    new MemNet.Client.PatchOperation("add", "/content/sdk_note", "sdk-note")
                ],
                Reason: "sdk_patch"),
            ifMatch: before.ETag);

        Assert.True(!string.Equals(before.ETag, patched.ETag, StringComparison.Ordinal), "Patch should update etag.");
        Assert.Equal("sdk-note", patched.Document.Content["sdk_note"]?.GetValue<string>());
    }

    private static async Task SdkClientMapsApiErrorsAsync()
    {
        using var scope = TestScope.Create();
        using var host = await ServiceHost.StartAsync(scope.RepoRoot, scope.DataRoot);
        using var client = new MemNet.Client.MemNetClient(new MemNet.Client.MemNetClientOptions
        {
            BaseAddress = host.BaseAddress,
            ServiceId = "sdk-tests"
        });

        var memScope = new MemNet.Client.MemNetScope(scope.Keys.Tenant, scope.Keys.User);
        var docRef = new MemNet.Client.DocumentRef("user", "long_term_memory.json");

        var before = await client.GetDocumentAsync(memScope, docRef);
        await client.PatchDocumentAsync(
            memScope,
            docRef,
            new MemNet.Client.PatchDocumentRequest(
                Ops:
                [
                    new MemNet.Client.PatchOperation("replace", "/content/preferences/0", "first")
                ],
                Reason: "sdk_patch"),
            ifMatch: before.ETag);

        try
        {
            await client.PatchDocumentAsync(
                memScope,
                docRef,
                new MemNet.Client.PatchDocumentRequest(
                    Ops:
                    [
                        new MemNet.Client.PatchOperation("replace", "/content/preferences/0", "stale")
                    ],
                    Reason: "sdk_patch"),
                ifMatch: before.ETag);
            throw new Exception("Expected stale patch to fail.");
        }
        catch (MemNet.Client.MemNetApiException ex)
        {
            Assert.Equal(HttpStatusCode.PreconditionFailed, ex.StatusCode);
            Assert.Equal("ETAG_MISMATCH", ex.Code);
            Assert.True(!string.IsNullOrWhiteSpace(ex.RequestId), "Expected request_id in API error payload.");
        }
    }

    private static async Task SdkClientAssembleAndSearchFlowWorksAsync()
    {
        using var scope = TestScope.Create();
        using var host = await ServiceHost.StartAsync(scope.RepoRoot, scope.DataRoot);
        using var client = new MemNet.Client.MemNetClient(new MemNet.Client.MemNetClientOptions
        {
            BaseAddress = host.BaseAddress,
            ServiceId = "sdk-tests"
        });

        var memScope = new MemNet.Client.MemNetScope(scope.Keys.Tenant, scope.Keys.User);

        var assembled = await client.AssembleContextAsync(
            memScope,
            new MemNet.Client.AssembleContextRequest(
                Documents:
                [
                    new MemNet.Client.AssembleDocumentRef("user", "profile.json"),
                    new MemNet.Client.AssembleDocumentRef("user", "long_term_memory.json")
                ],
                MaxDocs: 4,
                MaxCharsTotal: 30000));

        Assert.Equal(2, assembled.Documents.Count);
        Assert.Equal(0, assembled.DroppedDocuments.Count);

        await client.WriteEventAsync(
            memScope,
            new MemNet.Client.WriteEventRequest(
                new MemNet.Client.EventDigest(
                    EventId: "evt-sdk-1",
                    TenantId: scope.Keys.Tenant,
                    UserId: scope.Keys.User,
                    ServiceId: "sdk-tests",
                    Timestamp: DateTimeOffset.UtcNow,
                    SourceType: "chat",
                    Digest: "SDK client end to end event",
                    Keywords: ["sdk", "event"],
                    ProjectIds: ["project-alpha"],
                    SnapshotUri: "blob://snapshots/sdk",
                    Evidence: new MemNet.Client.EventEvidence(["m-sdk"], 1, 1))));

        var search = await client.SearchEventsAsync(
            memScope,
            new MemNet.Client.SearchEventsRequest(
                Query: "sdk client",
                ServiceId: "sdk-tests",
                SourceType: "chat",
                ProjectId: "project-alpha",
                From: null,
                To: null,
                TopK: 5));

        Assert.True(search.Results.Any(x => x.EventId == "evt-sdk-1"), "Expected SDK-written event to be searchable.");
    }
}
