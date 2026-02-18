using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace MemNet.MemoryService.IntegrationTests;

[Collection(MemNetApiTestCollection.Name)]
public sealed class ContextEventsLifecycleApiTests(MemNetApiFixture fixture)
{
    [Fact]
    public async Task AssembleContext_WithExplicitFiles_ReturnsRequestedFiles()
    {
        fixture.ResetDataRoot();
        await SeedDocumentAsync("user/profile.json", "profile");
        await SeedDocumentAsync("user/long_term_memory.json", "long-term");

        var response = await fixture.Client.PostAsJsonAsync(
            ApiTestHelpers.ContextAssembleRoute(fixture),
            new
            {
                files = new[]
                {
                    new { path = "user/profile.json" },
                    new { path = "user/long_term_memory.json" }
                },
                max_docs = 4,
                max_chars_total = 30_000
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await ApiTestHelpers.ReadJsonObjectAsync(response);
        var files = payload["files"]?.AsArray() ?? throw new Xunit.Sdk.XunitException("Expected files array.");
        var dropped = payload["dropped_files"]?.AsArray() ?? throw new Xunit.Sdk.XunitException("Expected dropped_files array.");

        Assert.Equal(2, files.Count);
        Assert.Empty(dropped);
    }

    [Fact]
    public async Task EventsWriteAndSearch_WorksEndToEnd()
    {
        fixture.ResetDataRoot();

        var response = await fixture.Client.PostAsJsonAsync(
            ApiTestHelpers.EventsRoute(fixture),
            new
            {
                @event = new
                {
                    event_id = "evt-context-search-1",
                    tenant_id = fixture.TenantId,
                    user_id = fixture.UserId,
                    service_id = "integration-tests",
                    timestamp = DateTimeOffset.UtcNow,
                    source_type = "chat",
                    digest = "Investigated latency in retrieval pipeline.",
                    keywords = new[] { "latency", "retrieval" },
                    project_ids = new[] { "project-a" },
                    evidence = new
                    {
                        source = "chat",
                        snapshot_uri = "blob://snapshots/1",
                        message_ids = new[] { "m1" }
                    }
                }
            });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var search = await fixture.Client.PostAsJsonAsync(
            ApiTestHelpers.EventsSearchRoute(fixture),
            new
            {
                query = "latency",
                service_id = "integration-tests",
                source_type = "chat",
                project_id = "project-a",
                top_k = 5
            });
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);

        var searchPayload = await ApiTestHelpers.ReadJsonObjectAsync(search);
        var results = searchPayload["results"]?.AsArray() ?? throw new Xunit.Sdk.XunitException("Expected results array.");
        Assert.True(results.Count >= 1);
        Assert.Equal("evt-context-search-1", results[0]?["event_id"]?.GetValue<string>());
    }

    [Fact]
    public async Task RetentionApply_RemovesExpiredEvents()
    {
        fixture.ResetDataRoot();

        await WriteEventAsync("evt-retention-old", DateTimeOffset.UtcNow.AddDays(-420), "old");
        await WriteEventAsync("evt-retention-fresh", DateTimeOffset.UtcNow.AddDays(-1), "fresh");

        var retention = await fixture.Client.PostAsJsonAsync(
            ApiTestHelpers.RetentionApplyRoute(fixture),
            new
            {
                events_days = 365,
                audit_days = 365,
                snapshots_days = 60,
                as_of_utc = (DateTimeOffset?)null
            });
        Assert.Equal(HttpStatusCode.OK, retention.StatusCode);

        var retentionPayload = await ApiTestHelpers.ReadJsonObjectAsync(retention);
        Assert.True((retentionPayload["events_deleted"]?.GetValue<int>() ?? 0) >= 1);

        var search = await fixture.Client.PostAsJsonAsync(
            ApiTestHelpers.EventsSearchRoute(fixture),
            new
            {
                query = (string?)null,
                top_k = 20
            });
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);

        var searchPayload = await ApiTestHelpers.ReadJsonObjectAsync(search);
        var results = searchPayload["results"]?.AsArray() ?? throw new Xunit.Sdk.XunitException("Expected results array.");
        var eventIds = results
            .Select(x => x?["event_id"]?.GetValue<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("evt-retention-old", eventIds);
        Assert.Contains("evt-retention-fresh", eventIds);
    }

    [Fact]
    public async Task ForgetUser_RemovesDocumentsAndEvents()
    {
        fixture.ResetDataRoot();
        await SeedDocumentAsync("user/long_term_memory.json", "forget-me");
        await WriteEventAsync("evt-forget-1", DateTimeOffset.UtcNow, "forget");

        var forget = await fixture.Client.DeleteAsync(ApiTestHelpers.ForgetUserRoute(fixture));
        Assert.Equal(HttpStatusCode.OK, forget.StatusCode);

        var forgetPayload = await ApiTestHelpers.ReadJsonObjectAsync(forget);
        Assert.True((forgetPayload["documents_deleted"]?.GetValue<int>() ?? 0) >= 1);
        Assert.True((forgetPayload["events_deleted"]?.GetValue<int>() ?? 0) >= 1);
        Assert.True((forgetPayload["audit_deleted"]?.GetValue<int>() ?? 0) >= 1);

        var getFile = await fixture.Client.GetAsync(ApiTestHelpers.FileRoute(fixture, "user/long_term_memory.json"));
        await ApiTestHelpers.AssertApiErrorAsync(getFile, HttpStatusCode.NotFound, "DOCUMENT_NOT_FOUND");

        var search = await fixture.Client.PostAsJsonAsync(
            ApiTestHelpers.EventsSearchRoute(fixture),
            new
            {
                query = (string?)null,
                top_k = 20
            });
        var searchPayload = await ApiTestHelpers.ReadJsonObjectAsync(search);
        var results = searchPayload["results"]?.AsArray() ?? throw new Xunit.Sdk.XunitException("Expected results array.");
        Assert.Empty(results);
    }

    private async Task SeedDocumentAsync(string path, string tag)
    {
        var response = await fixture.Client.SendAsync(
            ApiTestHelpers.CreatePutRequest(
                ApiTestHelpers.FileRoute(fixture, path),
                CreateReplaceBody(tag)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task WriteEventAsync(string eventId, DateTimeOffset timestamp, string keyword)
    {
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
                    timestamp,
                    source_type = "chat",
                    digest = $"event {eventId}",
                    keywords = new[] { keyword },
                    project_ids = new[] { "project-a" },
                    evidence = new JsonObject
                    {
                        ["source"] = "integration-tests",
                        ["snapshot_uri"] = $"blob://snapshots/{eventId}"
                    }
                }
            });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
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
                    text = $"seed:{tag}"
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
