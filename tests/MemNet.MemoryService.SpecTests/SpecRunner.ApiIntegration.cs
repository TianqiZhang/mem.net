using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

internal sealed partial class SpecRunner
{
    private static async Task HttpDocumentPatchFlowWorksEndToEndAsync()
    {
        using var scope = TestScope.Create();
        using var host = await ServiceHost.StartAsync(scope.RepoRoot, scope.DataRoot);
        using var client = CreateHttpClient(host.BaseAddress);

        var route = $"/v1/tenants/{scope.Keys.Tenant}/users/{scope.Keys.User}/documents/user/long_term_memory.json";

        var getResponse = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var getPayload = JsonNode.Parse(await getResponse.Content.ReadAsStringAsync())?.AsObject()
            ?? throw new Exception("Expected document payload.");
        var etag = getPayload["etag"]?.GetValue<string>();
        Assert.True(!string.IsNullOrWhiteSpace(etag), "Expected ETag from GET document.");

        var patchRequest = new HttpRequestMessage(HttpMethod.Patch, route)
        {
            Content = JsonContent.Create(new
            {
                ops = new[]
                {
                    new { op = "replace", path = "/content/preferences/0", value = "HTTP patch updated preference." }
                },
                reason = "live_update",
                evidence = new { conversation_id = "c-http", message_ids = new[] { "m1" }, snapshot_uri = (string?)null }
            })
        };
        patchRequest.Headers.TryAddWithoutValidation("If-Match", etag);
        patchRequest.Headers.TryAddWithoutValidation("X-Service-Id", "http-spec-tests");

        var patchResponse = await client.SendAsync(patchRequest);
        Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);

        var patchBody = JsonNode.Parse(await patchResponse.Content.ReadAsStringAsync())?.AsObject()
            ?? throw new Exception("Expected patch response payload.");
        var newEtag = patchBody["etag"]?.GetValue<string>();
        Assert.True(!string.IsNullOrWhiteSpace(newEtag), "Expected updated ETag from patch.");
        Assert.True(!string.Equals(etag, newEtag, StringComparison.Ordinal), "Expected ETag to change after patch.");

        var stalePatchRequest = new HttpRequestMessage(HttpMethod.Patch, route)
        {
            Content = JsonContent.Create(new
            {
                ops = new[]
                {
                    new { op = "replace", path = "/content/preferences/0", value = "stale write" }
                },
                reason = "live_update"
            })
        };
        stalePatchRequest.Headers.TryAddWithoutValidation("If-Match", etag);
        stalePatchRequest.Headers.TryAddWithoutValidation("X-Service-Id", "http-spec-tests");

        var staleResponse = await client.SendAsync(stalePatchRequest);
        Assert.Equal(HttpStatusCode.PreconditionFailed, staleResponse.StatusCode);
    }

    private static async Task HttpDocumentPatchAddOperationWorksEndToEndAsync()
    {
        using var scope = TestScope.Create();
        using var host = await ServiceHost.StartAsync(scope.RepoRoot, scope.DataRoot);
        using var client = CreateHttpClient(host.BaseAddress);

        var route = $"/v1/tenants/{scope.Keys.Tenant}/users/{scope.Keys.User}/documents/user/long_term_memory.json";

        var getResponse = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var getPayload = JsonNode.Parse(await getResponse.Content.ReadAsStringAsync())?.AsObject()
            ?? throw new Exception("Expected document payload.");
        var etag = getPayload["etag"]?.GetValue<string>();
        Assert.True(!string.IsNullOrWhiteSpace(etag), "Expected ETag from GET document.");

        var patchRequest = new HttpRequestMessage(HttpMethod.Patch, route)
        {
            Content = JsonContent.Create(new
            {
                ops = new[]
                {
                    new { op = "add", path = "/content/freeform_note", value = "HTTP patch add operation update." }
                },
                reason = "live_update",
                evidence = new { conversation_id = "c-http-add", message_ids = new[] { "m1" }, snapshot_uri = (string?)null }
            })
        };
        patchRequest.Headers.TryAddWithoutValidation("If-Match", etag);
        patchRequest.Headers.TryAddWithoutValidation("X-Service-Id", "http-spec-tests");

        var patchResponse = await client.SendAsync(patchRequest);
        Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);

        var patchBody = JsonNode.Parse(await patchResponse.Content.ReadAsStringAsync())?.AsObject()
            ?? throw new Exception("Expected patch response payload.");
        var note = patchBody["document"]?["content"]?["freeform_note"]?.GetValue<string>();
        Assert.Equal("HTTP patch add operation update.", note);
    }

    private static async Task HttpContextAssembleReturnsDefaultDocsAsync()
    {
        using var scope = TestScope.Create();
        using var host = await ServiceHost.StartAsync(scope.RepoRoot, scope.DataRoot);
        using var client = CreateHttpClient(host.BaseAddress);

        var contextResponse = await client.PostAsJsonAsync(
            $"/v1/tenants/{scope.Keys.Tenant}/users/{scope.Keys.User}/context:assemble",
            new
            {
                documents = new[]
                {
                    new { @namespace = "user", path = "profile.json" },
                    new { @namespace = "user", path = "long_term_memory.json" }
                },
                max_docs = 4,
                max_chars_total = 30000
            });

        Assert.Equal(HttpStatusCode.OK, contextResponse.StatusCode);
        var contextBody = JsonNode.Parse(await contextResponse.Content.ReadAsStringAsync())?.AsObject()
            ?? throw new Exception("Expected context response.");
        var contextDocs = contextBody["documents"] as JsonArray ?? throw new Exception("Expected documents array.");
        Assert.Equal(2, contextDocs.Count);

        var droppedDocs = contextBody["dropped_documents"] as JsonArray ?? throw new Exception("Expected dropped_documents array.");
        Assert.Equal(0, droppedDocs.Count);
    }

    private static async Task HttpContextAssembleWithExplicitDocumentsWorksAsync()
    {
        using var scope = TestScope.Create();
        using var host = await ServiceHost.StartAsync(scope.RepoRoot, scope.DataRoot);
        using var client = CreateHttpClient(host.BaseAddress);

        var contextResponse = await client.PostAsJsonAsync(
            $"/v1/tenants/{scope.Keys.Tenant}/users/{scope.Keys.User}/context:assemble",
            new
            {
                documents = new[]
                {
                    new { @namespace = "user", path = "profile.json" },
                    new { @namespace = "user", path = "long_term_memory.json" }
                },
                max_docs = 4,
                max_chars_total = 30000
            });

        Assert.Equal(HttpStatusCode.OK, contextResponse.StatusCode);
        var contextBody = JsonNode.Parse(await contextResponse.Content.ReadAsStringAsync())?.AsObject()
            ?? throw new Exception("Expected context response.");
        var contextDocs = contextBody["documents"] as JsonArray ?? throw new Exception("Expected documents array.");
        Assert.Equal(2, contextDocs.Count);

        var droppedDocs = contextBody["dropped_documents"] as JsonArray ?? throw new Exception("Expected dropped_documents array.");
        Assert.Equal(0, droppedDocs.Count);
    }

    private static async Task HttpEventsWriteAndSearchFlowWorksAsync()
    {
        using var scope = TestScope.Create();
        using var host = await ServiceHost.StartAsync(scope.RepoRoot, scope.DataRoot);
        using var client = CreateHttpClient(host.BaseAddress);

        var now = DateTimeOffset.UtcNow;
        var writeEventResponse = await client.PostAsJsonAsync(
            $"/v1/tenants/{scope.Keys.Tenant}/users/{scope.Keys.User}/events",
            new
            {
                @event = new
                {
                    event_id = "evt-http-1",
                    tenant_id = scope.Keys.Tenant,
                    user_id = scope.Keys.User,
                    service_id = "http-spec-tests",
                    timestamp = now,
                    source_type = "chat",
                    digest = "HTTP integration event for retrieval latency.",
                    keywords = new[] { "retrieval", "latency" },
                    project_ids = new[] { "project-alpha" },
                    snapshot_uri = "blob://snapshots/http",
                    evidence = new
                    {
                        message_ids = new[] { "m-http-1" },
                        start = 1,
                        end = 2
                    }
                }
            });
        Assert.Equal(HttpStatusCode.Accepted, writeEventResponse.StatusCode);

        var searchResponse = await client.PostAsJsonAsync(
            $"/v1/tenants/{scope.Keys.Tenant}/users/{scope.Keys.User}/events:search",
            new
            {
                query = "latency",
                service_id = "http-spec-tests",
                source_type = "chat",
                project_id = "project-alpha",
                top_k = 5
            });
        Assert.Equal(HttpStatusCode.OK, searchResponse.StatusCode);
        var searchBody = JsonNode.Parse(await searchResponse.Content.ReadAsStringAsync())?.AsObject()
            ?? throw new Exception("Expected event search response.");
        var results = searchBody["results"] as JsonArray ?? throw new Exception("Expected results array.");
        Assert.True(results.Count >= 1, "Expected at least one event search result.");
    }

#if !MEMNET_ENABLE_AZURE_SDK
    private static async Task AzureProviderDisabledReturns501Async()
    {
        using var scope = TestScope.Create();
        using var host = await ServiceHost.StartAsync(
            scope.RepoRoot,
            scope.DataRoot,
            provider: "azure");

        using var client = CreateHttpClient(host.BaseAddress);

        var response = await client.GetAsync($"/v1/tenants/{scope.Keys.Tenant}/users/{scope.Keys.User}/documents/user/long_term_memory.json");
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);

        var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync())?.AsObject()
            ?? throw new Exception("Expected error payload.");
        var errorCode = payload["error"]?["code"]?.GetValue<string>();
        Assert.Equal("AZURE_PROVIDER_NOT_ENABLED", errorCode);
    }
#endif

    private static async Task RetentionSweepRemovesExpiredEventsAsync()
    {
        using var scope = TestScope.Create();
        using var host = await ServiceHost.StartAsync(scope.RepoRoot, scope.DataRoot);
        using var client = CreateHttpClient(host.BaseAddress);

        var oldTimestamp = DateTimeOffset.UtcNow.AddDays(-420);
        var freshTimestamp = DateTimeOffset.UtcNow.AddDays(-1);
        var oldEventResponse = await client.PostAsJsonAsync(
            $"/v1/tenants/{scope.Keys.Tenant}/users/{scope.Keys.User}/events",
            new
            {
                @event = new
                {
                    event_id = "evt-retention-old",
                    tenant_id = scope.Keys.Tenant,
                    user_id = scope.Keys.User,
                    service_id = "retention-tests",
                    timestamp = oldTimestamp,
                    source_type = "chat",
                    digest = "Old event",
                    keywords = new[] { "old" },
                    project_ids = new[] { "project-alpha" },
                    snapshot_uri = "blob://snapshots/old",
                    evidence = new { message_ids = new[] { "m-old" }, start = 1, end = 1 }
                }
            });
        Assert.Equal(HttpStatusCode.Accepted, oldEventResponse.StatusCode);

        var freshEventResponse = await client.PostAsJsonAsync(
            $"/v1/tenants/{scope.Keys.Tenant}/users/{scope.Keys.User}/events",
            new
            {
                @event = new
                {
                    event_id = "evt-retention-fresh",
                    tenant_id = scope.Keys.Tenant,
                    user_id = scope.Keys.User,
                    service_id = "retention-tests",
                    timestamp = freshTimestamp,
                    source_type = "chat",
                    digest = "Fresh event",
                    keywords = new[] { "fresh" },
                    project_ids = new[] { "project-alpha" },
                    snapshot_uri = "blob://snapshots/fresh",
                    evidence = new { message_ids = new[] { "m-fresh" }, start = 1, end = 1 }
                }
            });
        Assert.Equal(HttpStatusCode.Accepted, freshEventResponse.StatusCode);

        var retentionResponse = await client.PostAsJsonAsync(
            $"/v1/tenants/{scope.Keys.Tenant}/users/{scope.Keys.User}/retention:apply",
            new
            {
                events_days = 365,
                audit_days = 365,
                snapshots_days = 60,
                as_of_utc = (DateTimeOffset?)null
            });
        Assert.Equal(HttpStatusCode.OK, retentionResponse.StatusCode);

        var retentionBody = JsonNode.Parse(await retentionResponse.Content.ReadAsStringAsync())?.AsObject()
            ?? throw new Exception("Expected retention response.");
        Assert.True((retentionBody["events_deleted"]?.GetValue<int>() ?? 0) >= 1, "Expected at least one old event to be deleted.");

        var searchResponse = await client.PostAsJsonAsync(
            $"/v1/tenants/{scope.Keys.Tenant}/users/{scope.Keys.User}/events:search",
            new { query = (string?)null, top_k = 20 });
        Assert.Equal(HttpStatusCode.OK, searchResponse.StatusCode);

        var searchBody = JsonNode.Parse(await searchResponse.Content.ReadAsStringAsync())?.AsObject()
            ?? throw new Exception("Expected event search response.");
        var results = searchBody["results"] as JsonArray ?? throw new Exception("Expected results array.");
        var eventIds = results
            .Select(r => r?["event_id"]?.GetValue<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(!eventIds.Contains("evt-retention-old"), "Expired event should have been removed.");
        Assert.True(eventIds.Contains("evt-retention-fresh"), "Fresh event should remain after retention sweep.");
    }

    private static async Task RetentionSweepRequestShapeWorksAsync()
    {
        using var scope = TestScope.Create();
        using var host = await ServiceHost.StartAsync(scope.RepoRoot, scope.DataRoot);
        using var client = CreateHttpClient(host.BaseAddress);

        var oldTimestamp = DateTimeOffset.UtcNow.AddDays(-420);
        var writeEventResponse = await client.PostAsJsonAsync(
            $"/v1/tenants/{scope.Keys.Tenant}/users/{scope.Keys.User}/events",
            new
            {
                @event = new
                {
                    event_id = "evt-retention-shape-old",
                    tenant_id = scope.Keys.Tenant,
                    user_id = scope.Keys.User,
                    service_id = "retention-tests",
                    timestamp = oldTimestamp,
                    source_type = "chat",
                    digest = "Old event retention request-shape test",
                    keywords = new[] { "old" },
                    project_ids = new[] { "project-alpha" },
                    snapshot_uri = "blob://snapshots/old-shape",
                    evidence = new { message_ids = new[] { "m-old-shape" }, start = 1, end = 1 }
                }
            });
        Assert.Equal(HttpStatusCode.Accepted, writeEventResponse.StatusCode);

        var retentionResponse = await client.PostAsJsonAsync(
            $"/v1/tenants/{scope.Keys.Tenant}/users/{scope.Keys.User}/retention:apply",
            new
            {
                events_days = 365,
                audit_days = 365,
                snapshots_days = 60,
                as_of_utc = (DateTimeOffset?)null
            });
        Assert.Equal(HttpStatusCode.OK, retentionResponse.StatusCode);

        var searchResponse = await client.PostAsJsonAsync(
            $"/v1/tenants/{scope.Keys.Tenant}/users/{scope.Keys.User}/events:search",
            new { query = (string?)null, top_k = 20 });
        Assert.Equal(HttpStatusCode.OK, searchResponse.StatusCode);

        var searchBody = JsonNode.Parse(await searchResponse.Content.ReadAsStringAsync())?.AsObject()
            ?? throw new Exception("Expected event search response.");
        var results = searchBody["results"] as JsonArray ?? throw new Exception("Expected results array.");
        var eventIds = results
            .Select(r => r?["event_id"]?.GetValue<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(!eventIds.Contains("evt-retention-shape-old"), "Expired event should have been removed by retention request.");
    }

    private static async Task ForgetUserRemovesDocumentsAndEventsAsync()
    {
        using var scope = TestScope.Create();
        using var host = await ServiceHost.StartAsync(scope.RepoRoot, scope.DataRoot);
        using var client = CreateHttpClient(host.BaseAddress);

        var route = $"/v1/tenants/{scope.Keys.Tenant}/users/{scope.Keys.User}/documents/user/long_term_memory.json";

        var getResponse = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var getPayload = JsonNode.Parse(await getResponse.Content.ReadAsStringAsync())?.AsObject()
            ?? throw new Exception("Expected seeded document.");
        var etag = getPayload["etag"]?.GetValue<string>() ?? throw new Exception("Expected seeded ETag.");

        var patchRequest = new HttpRequestMessage(HttpMethod.Patch, route)
        {
            Content = JsonContent.Create(new
            {
                ops = new[] { new { op = "replace", path = "/content/preferences/0", value = "forget flow test" } },
                reason = "live_update"
            })
        };
        patchRequest.Headers.TryAddWithoutValidation("If-Match", etag);
        patchRequest.Headers.TryAddWithoutValidation("X-Service-Id", "spec-tests");

        var patchResponse = await client.SendAsync(patchRequest);
        Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);

        var eventResponse = await client.PostAsJsonAsync(
            $"/v1/tenants/{scope.Keys.Tenant}/users/{scope.Keys.User}/events",
            new
            {
                @event = new
                {
                    event_id = "evt-forget-1",
                    tenant_id = scope.Keys.Tenant,
                    user_id = scope.Keys.User,
                    service_id = "forget-tests",
                    timestamp = DateTimeOffset.UtcNow,
                    source_type = "chat",
                    digest = "Forget flow event",
                    keywords = new[] { "forget" },
                    project_ids = new[] { "project-alpha" },
                    snapshot_uri = "blob://snapshots/forget",
                    evidence = new { message_ids = new[] { "m-forget" }, start = 1, end = 1 }
                }
            });
        Assert.Equal(HttpStatusCode.Accepted, eventResponse.StatusCode);

        var forgetResponse = await client.DeleteAsync($"/v1/tenants/{scope.Keys.Tenant}/users/{scope.Keys.User}/memory");
        Assert.Equal(HttpStatusCode.OK, forgetResponse.StatusCode);

        var forgetBody = JsonNode.Parse(await forgetResponse.Content.ReadAsStringAsync())?.AsObject()
            ?? throw new Exception("Expected forget response.");
        Assert.True((forgetBody["documents_deleted"]?.GetValue<int>() ?? 0) >= 1, "Expected documents to be deleted.");
        Assert.True((forgetBody["events_deleted"]?.GetValue<int>() ?? 0) >= 1, "Expected events to be deleted.");
        Assert.True((forgetBody["audit_deleted"]?.GetValue<int>() ?? 0) >= 1, "Expected audit records to be deleted.");

        var getAfterForget = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.NotFound, getAfterForget.StatusCode);

        var searchAfterForget = await client.PostAsJsonAsync(
            $"/v1/tenants/{scope.Keys.Tenant}/users/{scope.Keys.User}/events:search",
            new { query = (string?)null, top_k = 20 });
        Assert.Equal(HttpStatusCode.OK, searchAfterForget.StatusCode);

        var searchBody = JsonNode.Parse(await searchAfterForget.Content.ReadAsStringAsync())?.AsObject()
            ?? throw new Exception("Expected search response.");
        var results = searchBody["results"] as JsonArray ?? throw new Exception("Expected results array.");
        Assert.True(results.Count == 0, "Expected no events after forget-user delete.");
    }

    private static HttpClient CreateHttpClient(Uri baseAddress)
    {
        return new HttpClient
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromSeconds(10)
        };
    }
}
