using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace MemNet.MemoryService.IntegrationTests;

internal static class ApiTestHelpers
{
    public static string FileRoute(MemNetApiFixture fixture, string path) =>
        $"/v1/tenants/{fixture.TenantId}/users/{fixture.UserId}/files/{path}";

    public static string EventsRoute(MemNetApiFixture fixture) =>
        $"/v1/tenants/{fixture.TenantId}/users/{fixture.UserId}/events";

    public static string EventsSearchRoute(MemNetApiFixture fixture) =>
        $"/v1/tenants/{fixture.TenantId}/users/{fixture.UserId}/events:search";

    public static string ContextAssembleRoute(MemNetApiFixture fixture) =>
        $"/v1/tenants/{fixture.TenantId}/users/{fixture.UserId}/context:assemble";

    public static string RetentionApplyRoute(MemNetApiFixture fixture) =>
        $"/v1/tenants/{fixture.TenantId}/users/{fixture.UserId}/retention:apply";

    public static string ForgetUserRoute(MemNetApiFixture fixture) =>
        $"/v1/tenants/{fixture.TenantId}/users/{fixture.UserId}/memory";

    public static HttpRequestMessage CreatePatchRequest(string route, string etag, object body, string actor = "integration-tests")
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, route)
        {
            Content = JsonContent.Create(body)
        };
        if (!string.IsNullOrWhiteSpace(etag))
        {
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        }
        request.Headers.TryAddWithoutValidation("X-Service-Id", actor);
        return request;
    }

    public static HttpRequestMessage CreatePutRequest(string route, object body, string? etag = "*", string actor = "integration-tests")
    {
        var request = new HttpRequestMessage(HttpMethod.Put, route)
        {
            Content = JsonContent.Create(body)
        };
        if (!string.IsNullOrWhiteSpace(etag))
        {
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        }
        request.Headers.TryAddWithoutValidation("X-Service-Id", actor);
        return request;
    }

    public static async Task<JsonObject> ReadJsonObjectAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonNode.Parse(body)?.AsObject()
            ?? throw new Xunit.Sdk.XunitException($"Expected JSON object payload. Body: {body}");
    }

    public static async Task AssertApiErrorAsync(HttpResponseMessage response, HttpStatusCode expectedStatus, string expectedCode)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        var payload = await ReadJsonObjectAsync(response);
        Assert.Equal(expectedCode, payload["error"]?["code"]?.GetValue<string>());
    }
}
