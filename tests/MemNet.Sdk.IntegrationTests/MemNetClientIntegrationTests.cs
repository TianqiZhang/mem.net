using System.Net;
using System.Text.Json.Nodes;
using MemNet.Client;

namespace MemNet.Sdk.IntegrationTests;

[Collection(MemNetApiTestCollection.Name)]
public sealed class MemNetClientIntegrationTests(MemNetApiFixture fixture)
{
    [Fact]
    public async Task PatchAndGetFlow_Works()
    {
        fixture.ResetDataRoot();
        using var client = SdkTestData.CreateClient(fixture.Client);

        var scope = new MemNetScope(fixture.TenantId, fixture.UserId);
        await SdkTestData.SeedDefaultUserFilesAsync(client, scope);

        var file = new FileRef("user/long_term_memory.json");
        var before = await client.GetFileAsync(scope, file);

        var patched = await client.PatchFileAsync(
            scope,
            file,
            new PatchDocumentRequest(
                Ops:
                [
                    new PatchOperation("add", "/content/sdk_note", JsonValue.Create("sdk-note"))
                ],
                Reason: "sdk_patch"),
            ifMatch: before.ETag);

        Assert.NotEqual(before.ETag, patched.ETag);
        Assert.Equal("sdk-note", patched.Document.Content["sdk_note"]?.GetValue<string>());
    }

    [Fact]
    public async Task ListFiles_ByPrefix_Works()
    {
        fixture.ResetDataRoot();
        using var client = SdkTestData.CreateClient(fixture.Client);

        var scope = new MemNetScope(fixture.TenantId, fixture.UserId);
        await SdkTestData.SeedDefaultUserFilesAsync(client, scope);

        await client.WriteFileAsync(
            scope,
            new FileRef("projects/alpha.md"),
            new ReplaceDocumentRequest(
                Document: new DocumentEnvelope(
                    DocId: $"doc-{Guid.NewGuid():N}",
                    SchemaId: "memnet.file",
                    SchemaVersion: "1.0.0",
                    CreatedAt: DateTimeOffset.UtcNow,
                    UpdatedAt: DateTimeOffset.UtcNow,
                    UpdatedBy: "sdk-tests",
                    Content: new JsonObject
                    {
                        ["content_type"] = "text/markdown",
                        ["text"] = "# Alpha\n"
                    }),
                Reason: "seed_project"),
            ifMatch: "*");

        var listed = await client.ListFilesAsync(
            scope,
            new ListFilesRequest(Prefix: "projects/", Limit: 20));

        Assert.Contains(listed.Files, x => x.Path == "projects/alpha.md");
    }

    [Fact]
    public async Task ApiErrors_AreMappedToMemNetApiException()
    {
        fixture.ResetDataRoot();
        using var client = SdkTestData.CreateClient(fixture.Client);

        var scope = new MemNetScope(fixture.TenantId, fixture.UserId);
        await SdkTestData.SeedDefaultUserFilesAsync(client, scope);

        var file = new FileRef("user/long_term_memory.json");
        var before = await client.GetFileAsync(scope, file);

        await client.PatchFileAsync(
            scope,
            file,
            new PatchDocumentRequest(
                Ops:
                [
                    new PatchOperation("replace", "/content/preferences/0", JsonValue.Create("first"))
                ],
                Reason: "sdk_patch"),
            ifMatch: before.ETag);

        var ex = await Assert.ThrowsAsync<MemNetApiException>(
            () => client.PatchFileAsync(
                scope,
                file,
                new PatchDocumentRequest(
                    Ops:
                    [
                        new PatchOperation("replace", "/content/preferences/0", JsonValue.Create("stale"))
                    ],
                    Reason: "sdk_patch"),
                ifMatch: before.ETag));

        Assert.Equal(HttpStatusCode.PreconditionFailed, ex.StatusCode);
        Assert.Equal("ETAG_MISMATCH", ex.Code);
        Assert.False(string.IsNullOrWhiteSpace(ex.RequestId));
    }

    [Fact]
    public async Task AssembleAndSearchFlow_Works()
    {
        fixture.ResetDataRoot();
        using var client = SdkTestData.CreateClient(fixture.Client);

        var scope = new MemNetScope(fixture.TenantId, fixture.UserId);
        await SdkTestData.SeedDefaultUserFilesAsync(client, scope);

        var assembled = await client.AssembleContextAsync(
            scope,
            new AssembleContextRequest(
                Files:
                [
                    new AssembleFileRef("user/profile.json"),
                    new AssembleFileRef("user/long_term_memory.json")
                ],
                MaxDocs: 4,
                MaxCharsTotal: 30_000));

        Assert.Equal(2, assembled.Files.Count);
        Assert.Empty(assembled.DroppedFiles);

        await client.WriteEventAsync(
            scope,
            new WriteEventRequest(
                new EventDigest(
                    EventId: "evt-sdk-1",
                    TenantId: fixture.TenantId,
                    UserId: fixture.UserId,
                    ServiceId: "sdk-tests",
                    Timestamp: DateTimeOffset.UtcNow,
                    SourceType: "chat",
                    Digest: "SDK integration event",
                    Keywords: ["sdk", "event"],
                    ProjectIds: ["project-a"],
                    Evidence: new JsonObject
                    {
                        ["source"] = "sdk-tests",
                        ["message_ids"] = new JsonArray("m-sdk-1")
                    })));

        var search = await client.SearchEventsAsync(
            scope,
            new SearchEventsRequest(
                Query: "sdk",
                ServiceId: "sdk-tests",
                SourceType: "chat",
                ProjectId: "project-a",
                From: null,
                To: null,
                TopK: 5));

        Assert.Contains(search.Results, x => x.EventId == "evt-sdk-1");
    }

    [Fact]
    public async Task UpdateWithRetry_ResolvesEtagConflict()
    {
        fixture.ResetDataRoot();
        using var client = SdkTestData.CreateClient(fixture.Client);

        var scope = new MemNetScope(fixture.TenantId, fixture.UserId);
        await SdkTestData.SeedDefaultUserFilesAsync(client, scope);
        var file = new FileRef("user/long_term_memory.json");
        var injectedConflict = false;

        var updated = await client.UpdateWithRetryAsync(
            scope,
            file,
            current =>
            {
                if (!injectedConflict)
                {
                    injectedConflict = true;
                    client.PatchFileAsync(
                        scope,
                        file,
                        new PatchDocumentRequest(
                            Ops:
                            [
                                new PatchOperation("replace", "/content/preferences/0", JsonValue.Create("intermediate"))
                            ],
                            Reason: "inject_conflict"),
                        current.ETag).GetAwaiter().GetResult();
                }

                return FileUpdate.FromPatch(
                    new PatchDocumentRequest(
                        Ops:
                        [
                            new PatchOperation("replace", "/content/preferences/0", JsonValue.Create("final"))
                        ],
                        Reason: "retry_update"));
            },
            maxConflictRetries: 3);

        Assert.True(injectedConflict);
        Assert.Equal("final", updated.Document.Content["preferences"]?[0]?.GetValue<string>());
    }
}
