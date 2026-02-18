using System.Net;

namespace MemNet.MemoryService.IntegrationTests;

[Collection(MemNetApiTestCollection.Name)]
public sealed class FileApiContractTests(MemNetApiFixture fixture)
{
    [Fact]
    public async Task PutAndGetFile_WorksEndToEnd()
    {
        fixture.ResetDataRoot();

        var route = ApiTestHelpers.FileRoute(fixture, "user/long_term_memory.json");
        var putResponse = await fixture.Client.SendAsync(
            ApiTestHelpers.CreatePutRequest(route, CreateReplaceBody("put-get")));
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        var putPayload = await ApiTestHelpers.ReadJsonObjectAsync(putResponse);
        var etag = putPayload["etag"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(etag));

        var getResponse = await fixture.Client.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var getPayload = await ApiTestHelpers.ReadJsonObjectAsync(getResponse);

        Assert.Equal(etag, getPayload["etag"]?.GetValue<string>());
        Assert.Equal("put-get", getPayload["document"]?["content"]?["tag"]?.GetValue<string>());
    }

    [Fact]
    public async Task PatchFile_WithStaleEtag_ReturnsPreconditionFailed()
    {
        fixture.ResetDataRoot();

        var route = ApiTestHelpers.FileRoute(fixture, "user/long_term_memory.json");
        var putResponse = await fixture.Client.SendAsync(
            ApiTestHelpers.CreatePutRequest(route, CreateReplaceBody("before-patch")));
        var putPayload = await ApiTestHelpers.ReadJsonObjectAsync(putResponse);
        var etag = putPayload["etag"]?.GetValue<string>() ?? throw new Xunit.Sdk.XunitException("Expected etag.");

        var patchResponse = await fixture.Client.SendAsync(
            ApiTestHelpers.CreatePatchRequest(
                route,
                etag,
                new
                {
                    ops = new[]
                    {
                        new { op = "replace", path = "/content/tag", value = "patched" }
                    },
                    reason = "live_update"
                }));
        Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);

        var staleResponse = await fixture.Client.SendAsync(
            ApiTestHelpers.CreatePatchRequest(
                route,
                etag,
                new
                {
                    ops = new[]
                    {
                        new { op = "replace", path = "/content/tag", value = "stale" }
                    },
                    reason = "live_update"
                }));

        await ApiTestHelpers.AssertApiErrorAsync(staleResponse, HttpStatusCode.PreconditionFailed, "ETAG_MISMATCH");
    }

    [Fact]
    public async Task PatchFile_InvalidPath_ReturnsUnprocessableEntity()
    {
        fixture.ResetDataRoot();

        var route = ApiTestHelpers.FileRoute(fixture, "user/long_term_memory.json");
        var putResponse = await fixture.Client.SendAsync(
            ApiTestHelpers.CreatePutRequest(route, CreateReplaceBody("invalid-path")));
        var putPayload = await ApiTestHelpers.ReadJsonObjectAsync(putResponse);
        var etag = putPayload["etag"]?.GetValue<string>() ?? throw new Xunit.Sdk.XunitException("Expected etag.");

        var patchResponse = await fixture.Client.SendAsync(
            ApiTestHelpers.CreatePatchRequest(
                route,
                etag,
                new
                {
                    ops = new[]
                    {
                        new { op = "replace", path = "/content/not_found/0", value = "bad" }
                    },
                    reason = "live_update"
                }));

        await ApiTestHelpers.AssertApiErrorAsync(patchResponse, HttpStatusCode.UnprocessableEntity, "INVALID_PATCH_PATH");
    }

    [Fact]
    public async Task PutFile_WithoutIfMatch_ReturnsBadRequest()
    {
        fixture.ResetDataRoot();

        var route = ApiTestHelpers.FileRoute(fixture, "user/profile.json");
        var response = await fixture.Client.SendAsync(
            ApiTestHelpers.CreatePutRequest(route, CreateReplaceBody("missing-if-match"), etag: null));

        await ApiTestHelpers.AssertApiErrorAsync(response, HttpStatusCode.BadRequest, "MISSING_IF_MATCH");
    }

    private static object CreateReplaceBody(string tag)
    {
        var now = DateTimeOffset.UtcNow;
        return new
        {
            document = new
            {
                doc_id = $"doc-{Guid.NewGuid():N}",
                schema_id = "memnet.file",
                schema_version = "1.0.0",
                created_at = now,
                updated_at = now,
                updated_by = "seed",
                content = new
                {
                    tag,
                    preferences = new[] { "concise" }
                }
            },
            reason = "seed",
            evidence = new
            {
                source = "integration-tests"
            }
        };
    }
}
