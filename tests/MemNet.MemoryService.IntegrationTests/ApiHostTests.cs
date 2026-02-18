using System.Net;
using System.Net.Http.Json;
using MemNet.MemoryService.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace MemNet.MemoryService.IntegrationTests;

[Collection(MemNetApiTestCollection.Name)]
public sealed class ApiHostTests(MemNetApiFixture fixture)
{
    [Fact]
    public async Task HealthEndpoint_ReturnsServiceOk()
    {
        fixture.ResetDataRoot();

        var response = await fixture.Client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await ApiTestHelpers.ReadJsonObjectAsync(response);
        Assert.Equal("mem.net", payload["service"]?.GetValue<string>());
        Assert.Equal("ok", payload["status"]?.GetValue<string>());
    }

    [Fact]
    public async Task AssembleContext_EmptyFiles_ReturnsApiErrorEnvelope()
    {
        fixture.ResetDataRoot();

        var response = await fixture.Client.PostAsJsonAsync(
            ApiTestHelpers.ContextAssembleRoute(fixture),
            new
            {
                files = Array.Empty<object>(),
                max_docs = 4,
                max_chars_total = 4096
            });

        await ApiTestHelpers.AssertApiErrorAsync(response, HttpStatusCode.BadRequest, "MISSING_ASSEMBLY_TARGETS");
    }

    [Fact]
    public async Task EventsWrite_UsesConfiguredDataRoot()
    {
        fixture.ResetDataRoot();

        const string eventId = "evt-int-write-1";
        var response = await fixture.Client.PostAsJsonAsync(
            ApiTestHelpers.EventsRoute(fixture),
            new
            {
                @event = new
                {
                    event_id = eventId,
                    tenant_id = fixture.TenantId,
                    user_id = fixture.UserId,
                    service_id = "integration-tests",
                    timestamp = DateTimeOffset.UtcNow,
                    source_type = "chat",
                    digest = "integration write event",
                    keywords = new[] { "integration" },
                    project_ids = Array.Empty<string>(),
                    evidence = new { source = "integration-tests" }
                }
            });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var options = fixture.Factory.Services.GetRequiredService<StorageOptions>();
        Assert.Equal(fixture.DataRoot, options.DataRoot);
    }
}
