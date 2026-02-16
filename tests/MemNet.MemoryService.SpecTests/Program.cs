using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using MemNet.MemoryService.Core;
using MemNet.MemoryService.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

var runner = new SpecRunner();
await runner.RunAsync();

internal sealed class SpecRunner
{
    private readonly List<Func<Task>> _tests;

    public SpecRunner()
    {
        _tests =
        [
            FileStoreContractsAreConsistentAsync,
            PatchDocumentHappyPathAsync,
            PatchDocumentReturns412OnEtagMismatchAsync,
            PatchDocumentReturns422OnPathPolicyViolationAsync,
            AssembleContextIncludesDefaultDocsAndRespectsBudgetsAsync,
            EventSearchReturnsRelevantResultsAsync,
            HttpEndpointsWorkEndToEndAsync,
#if !MEMNET_ENABLE_AZURE_SDK
            AzureProviderDisabledReturns501Async,
#endif
            RetentionSweepRemovesExpiredEventsAsync,
            ForgetUserRemovesDocumentsAndEventsAsync,
#if MEMNET_ENABLE_AZURE_SDK
            AzureProviderOptionsMappingAndValidationAsync,
            AzureSearchFilterRequestShapingAsync,
#endif
            AzureLiveSmokeTestIfConfiguredAsync
        ];
    }

    public async Task RunAsync()
    {
        var passed = 0;
        foreach (var test in _tests)
        {
            try
            {
                await test();
                passed++;
                Console.WriteLine($"PASS {test.Method.Name}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FAIL {test.Method.Name}: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }

        Console.WriteLine($"{passed}/{_tests.Count} tests passed.");
        if (passed != _tests.Count)
        {
            Environment.ExitCode = 1;
        }
    }

    private static async Task FileStoreContractsAreConsistentAsync()
    {
        using var scope = TestScope.Create();
        var options = new StorageOptions
        {
            DataRoot = scope.DataRoot,
            ConfigRoot = scope.ConfigRoot
        };

        await RunDocumentStoreContractAsync(new FileDocumentStore(options));
        await RunEventStoreContractAsync(new FileEventStore(options), scope.Keys.Tenant, scope.Keys.User);
        await RunAuditStoreContractAsync(new FileAuditStore(options), options.DataRoot, scope.Keys.Tenant, scope.Keys.User);
    }

    private static async Task RunDocumentStoreContractAsync(IDocumentStore documentStore)
    {
        var key = new DocumentKey("tenant-contract", "user-contract", "user", "contract.json");
        var now = DateTimeOffset.UtcNow;
        var initial = new DocumentEnvelope(
            DocId: "doc-contract",
            SchemaId: "memory.contract",
            SchemaVersion: "1.0.0",
            CreatedAt: now,
            UpdatedAt: now,
            UpdatedBy: "spec-tests",
            Content: new JsonObject { ["value"] = "v1" });

        var created = await documentStore.UpsertAsync(key, initial, "*");
        Assert.True(!string.IsNullOrWhiteSpace(created.ETag), "Document contract expected non-empty ETag.");
        Assert.True(await documentStore.ExistsAsync(key), "Document contract expected ExistsAsync=true after create.");

        var fetched = await documentStore.GetAsync(key) ?? throw new Exception("Document contract expected fetched document.");
        Assert.Equal("v1", fetched.Envelope.Content["value"]?.GetValue<string>());

        await Assert.ThrowsAsync<ApiException>(
            async () =>
            {
                await documentStore.UpsertAsync(
                    key,
                    fetched.Envelope with { Content = new JsonObject { ["value"] = "v2-stale" } },
                    ifMatch: "\"stale\"");
            },
            ex => ex.StatusCode == 412 && ex.Code == "ETAG_MISMATCH");

        var updatedEnvelope = fetched.Envelope with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            Content = new JsonObject { ["value"] = "v2" }
        };
        var updated = await documentStore.UpsertAsync(key, updatedEnvelope, fetched.ETag);
        Assert.True(!string.Equals(updated.ETag, fetched.ETag, StringComparison.Ordinal), "Document contract expected ETag change on update.");
    }

    private static async Task RunEventStoreContractAsync(IEventStore eventStore, string tenantId, string userId)
    {
        await eventStore.WriteAsync(new EventDigest(
            EventId: "evt-contract-1",
            TenantId: tenantId,
            UserId: userId,
            ServiceId: "contract-tests",
            Timestamp: DateTimeOffset.UtcNow.AddMinutes(-5),
            SourceType: "chat",
            Digest: "Event contract baseline for retrieval latency.",
            Keywords: ["retrieval", "latency"],
            ProjectIds: ["project-contract"],
            SnapshotUri: "blob://contract/1",
            Evidence: new EventEvidence(["m1"], 1, 1)));

        await eventStore.WriteAsync(new EventDigest(
            EventId: "evt-contract-2",
            TenantId: tenantId,
            UserId: userId,
            ServiceId: "contract-tests",
            Timestamp: DateTimeOffset.UtcNow,
            SourceType: "chat",
            Digest: "Unrelated event digest.",
            Keywords: ["general"],
            ProjectIds: ["project-other"],
            SnapshotUri: "blob://contract/2",
            Evidence: new EventEvidence(["m2"], 1, 2)));

        var filtered = await eventStore.QueryAsync(
            tenantId,
            userId,
            new EventSearchRequest(
                Query: "latency",
                ServiceId: "contract-tests",
                SourceType: "chat",
                ProjectId: "project-contract",
                From: null,
                To: null,
                TopK: 5));

        Assert.True(filtered.Count == 1, "Event contract expected exactly one filtered event.");
        Assert.Equal("evt-contract-1", filtered[0].EventId);
    }

    private static async Task RunAuditStoreContractAsync(
        IAuditStore auditStore,
        string dataRoot,
        string tenantId,
        string userId)
    {
        var record = new AuditRecord(
            ChangeId: "chg-contract-1",
            Actor: "contract-tests",
            TenantId: tenantId,
            UserId: userId,
            Namespace: "user",
            Path: "long_term_memory.json",
            PreviousETag: "\"prev\"",
            NewETag: "\"new\"",
            Reason: "contract-check",
            Ops: [new PatchOperation("replace", "/content/preferences/0", JsonValue.Create("contract"))],
            Timestamp: DateTimeOffset.UtcNow,
            EvidenceMessageIds: ["m-audit-1"]);

        await auditStore.WriteAsync(record);

        var expectedPath = Path.Combine(dataRoot, "tenants", tenantId, "users", userId, "audit", "chg-contract-1.json");
        Assert.True(File.Exists(expectedPath), "Audit contract expected persisted audit file.");
    }

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

    private static async Task AssembleContextIncludesDefaultDocsAndRespectsBudgetsAsync()
    {
        using var scope = TestScope.Create();

        var full = await scope.Coordinator.AssembleContextAsync(
            tenantId: scope.Keys.Tenant,
            userId: scope.Keys.User,
            request: new AssembleContextRequest(
                PolicyId: "project-copilot-v1",
                MaxDocs: 5,
                MaxCharsTotal: 30000));

        Assert.Equal(2, full.Documents.Count);
        Assert.True(!full.Documents.Any(x => x.BindingId == "project_memory"), "Project document should not be included by default.");

        var tinyBudget = await scope.Coordinator.AssembleContextAsync(
            tenantId: scope.Keys.Tenant,
            userId: scope.Keys.User,
            request: new AssembleContextRequest(
                PolicyId: "project-copilot-v1",
                MaxDocs: 5,
                MaxCharsTotal: 300));

        Assert.True(tinyBudget.DroppedBindings.Count > 0, "Expected dropped_bindings when char budget is small.");
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

    private static async Task HttpEndpointsWorkEndToEndAsync()
    {
        using var scope = TestScope.Create();
        using var host = await ServiceHost.StartAsync(scope.RepoRoot, scope.DataRoot, scope.ConfigRoot);

        using var client = new HttpClient
        {
            BaseAddress = host.BaseAddress,
            Timeout = TimeSpan.FromSeconds(10)
        };

        var getResponse = await client.GetAsync($"/v1/tenants/{scope.Keys.Tenant}/users/{scope.Keys.User}/documents/user/long_term_memory.json");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var getPayload = JsonNode.Parse(await getResponse.Content.ReadAsStringAsync())?.AsObject()
            ?? throw new Exception("Expected document payload.");
        var etag = getPayload["etag"]?.GetValue<string>();
        Assert.True(!string.IsNullOrWhiteSpace(etag), "Expected ETag from GET document.");

        var patchPayload = new
        {
            policy_id = "project-copilot-v1",
            binding_id = "long_term_memory",
            ops = new[]
            {
                new { op = "replace", path = "/content/preferences/0", value = "HTTP patch updated preference." }
            },
            reason = "live_update",
            evidence = new { conversation_id = "c-http", message_ids = new[] { "m1" }, snapshot_uri = (string?)null }
        };

        var patchRequest = new HttpRequestMessage(HttpMethod.Patch, $"/v1/tenants/{scope.Keys.Tenant}/users/{scope.Keys.User}/documents/user/long_term_memory.json")
        {
            Content = JsonContent.Create(patchPayload)
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

        var stalePatchRequest = new HttpRequestMessage(HttpMethod.Patch, $"/v1/tenants/{scope.Keys.Tenant}/users/{scope.Keys.User}/documents/user/long_term_memory.json")
        {
            Content = JsonContent.Create(new
            {
                policy_id = "project-copilot-v1",
                binding_id = "long_term_memory",
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

        var contextResponse = await client.PostAsJsonAsync(
            $"/v1/tenants/{scope.Keys.Tenant}/users/{scope.Keys.User}/context:assemble",
            new
            {
                policy_id = "project-copilot-v1",
                max_docs = 4,
                max_chars_total = 30000
            });
        Assert.Equal(HttpStatusCode.OK, contextResponse.StatusCode);
        var contextBody = JsonNode.Parse(await contextResponse.Content.ReadAsStringAsync())?.AsObject()
            ?? throw new Exception("Expected context response.");
        var contextDocs = contextBody["documents"] as JsonArray ?? throw new Exception("Expected documents array.");
        Assert.Equal(2, contextDocs.Count);

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

#if MEMNET_ENABLE_AZURE_SDK
    private static Task AzureProviderOptionsMappingAndValidationAsync()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MemNet:Azure:StorageServiceUri"] = "https://example.blob.core.windows.net",
                ["MemNet:Azure:DocumentsContainerName"] = "docs",
                ["MemNet:Azure:EventsContainerName"] = "events",
                ["MemNet:Azure:AuditContainerName"] = "audit",
                ["MemNet:Azure:SearchEndpoint"] = "https://example.search.windows.net",
                ["MemNet:Azure:SearchIndexName"] = "events-index",
                ["MemNet:Azure:RetryMaxRetries"] = "5",
                ["MemNet:Azure:RetryDelayMs"] = "250",
                ["MemNet:Azure:RetryMaxDelayMs"] = "3000",
                ["MemNet:Azure:NetworkTimeoutSeconds"] = "45"
            })
            .Build();

        var options = AzureProviderOptions.FromConfiguration(config);
        Assert.Equal("https://example.blob.core.windows.net", options.StorageServiceUri);
        Assert.Equal("docs", options.DocumentsContainerName);
        Assert.Equal("events", options.EventsContainerName);
        Assert.Equal("audit", options.AuditContainerName);
        Assert.Equal("https://example.search.windows.net", options.SearchEndpoint);
        Assert.Equal("events-index", options.SearchIndexName);
        Assert.Equal(5, options.RetryMaxRetries);
        Assert.Equal(TimeSpan.FromMilliseconds(250), options.RetryDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(3000), options.RetryMaxDelay);
        Assert.Equal(TimeSpan.FromSeconds(45), options.NetworkTimeout);

        Assert.Throws<InvalidOperationException>(() =>
        {
            var invalidConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MemNet:Azure:StorageServiceUri"] = "https://example.blob.core.windows.net",
                    ["MemNet:Azure:SearchEndpoint"] = "https://example.search.windows.net"
                })
                .Build();

            _ = AzureProviderOptions.FromConfiguration(invalidConfig);
        });

        return Task.CompletedTask;
    }

    private static Task AzureSearchFilterRequestShapingAsync()
    {
        var from = new DateTimeOffset(2025, 01, 01, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero);
        var request = new EventSearchRequest(
            Query: "latency",
            ServiceId: "assistant'o",
            SourceType: "chat",
            ProjectId: "project'o",
            From: from,
            To: to,
            TopK: 10);

        var filter = AzureBlobEventStore.BuildFilter("tenant'o", "user'o", request);
        Assert.True(!string.IsNullOrWhiteSpace(filter), "Expected non-empty Azure filter.");
        var shapedFilter = filter!;
        Assert.True(shapedFilter.Contains("tenant_id eq 'tenant''o'", StringComparison.Ordinal), "Expected tenant filter escape.");
        Assert.True(shapedFilter.Contains("user_id eq 'user''o'", StringComparison.Ordinal), "Expected user filter escape.");
        Assert.True(shapedFilter.Contains("service_id eq 'assistant''o'", StringComparison.Ordinal), "Expected service filter escape.");
        Assert.True(shapedFilter.Contains("source_type eq 'chat'", StringComparison.Ordinal), "Expected source type filter.");
        Assert.True(shapedFilter.Contains("project_ids/any(p: p eq 'project''o')", StringComparison.Ordinal), "Expected project filter escape.");
        Assert.True(shapedFilter.Contains($"timestamp ge {from.UtcDateTime:O}", StringComparison.Ordinal), "Expected from timestamp filter.");
        Assert.True(shapedFilter.Contains($"timestamp le {to.UtcDateTime:O}", StringComparison.Ordinal), "Expected to timestamp filter.");
        return Task.CompletedTask;
    }
#endif

#if !MEMNET_ENABLE_AZURE_SDK
    private static async Task AzureProviderDisabledReturns501Async()
    {
        using var scope = TestScope.Create();
        using var host = await ServiceHost.StartAsync(
            scope.RepoRoot,
            scope.DataRoot,
            scope.ConfigRoot,
            provider: "azure");

        using var client = new HttpClient
        {
            BaseAddress = host.BaseAddress,
            Timeout = TimeSpan.FromSeconds(10)
        };

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
        using var host = await ServiceHost.StartAsync(scope.RepoRoot, scope.DataRoot, scope.ConfigRoot);

        using var client = new HttpClient
        {
            BaseAddress = host.BaseAddress,
            Timeout = TimeSpan.FromSeconds(10)
        };

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
            new { policy_id = "project-copilot-v1", as_of_utc = (DateTimeOffset?)null });
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

    private static async Task ForgetUserRemovesDocumentsAndEventsAsync()
    {
        using var scope = TestScope.Create();
        using var host = await ServiceHost.StartAsync(scope.RepoRoot, scope.DataRoot, scope.ConfigRoot);

        using var client = new HttpClient
        {
            BaseAddress = host.BaseAddress,
            Timeout = TimeSpan.FromSeconds(10)
        };

        var getResponse = await client.GetAsync($"/v1/tenants/{scope.Keys.Tenant}/users/{scope.Keys.User}/documents/user/long_term_memory.json");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var getPayload = JsonNode.Parse(await getResponse.Content.ReadAsStringAsync())?.AsObject()
            ?? throw new Exception("Expected seeded document.");
        var etag = getPayload["etag"]?.GetValue<string>() ?? throw new Exception("Expected seeded ETag.");

        var patchRequest = new HttpRequestMessage(HttpMethod.Patch, $"/v1/tenants/{scope.Keys.Tenant}/users/{scope.Keys.User}/documents/user/long_term_memory.json")
        {
            Content = JsonContent.Create(new
            {
                policy_id = "project-copilot-v1",
                binding_id = "long_term_memory",
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

        var getAfterForget = await client.GetAsync($"/v1/tenants/{scope.Keys.Tenant}/users/{scope.Keys.User}/documents/user/long_term_memory.json");
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

    private static async Task AzureLiveSmokeTestIfConfiguredAsync()
    {
        var runLive = Environment.GetEnvironmentVariable("MEMNET_RUN_AZURE_LIVE_TESTS");
        if (!string.Equals(runLive, "1", StringComparison.Ordinal))
        {
            return;
        }

        var requiredEnv = new[]
        {
            "MEMNET_AZURE_STORAGE_SERVICE_URI",
            "MEMNET_AZURE_DOCUMENTS_CONTAINER",
            "MEMNET_AZURE_EVENTS_CONTAINER",
            "MEMNET_AZURE_AUDIT_CONTAINER"
        };

        var missing = requiredEnv
            .Where(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
            .ToList();
        if (missing.Count > 0)
        {
            throw new Exception($"Azure live test requested, but required env vars are missing: {string.Join(", ", missing)}");
        }

        using var scope = TestScope.Create();
        var liveDll = Path.Combine(scope.RepoRoot, "src", "MemNet.MemoryService", "bin", "Debug", "azure", "net8.0", "MemNet.MemoryService.dll");
        if (!File.Exists(liveDll))
        {
            throw new Exception($"Azure-enabled service DLL not found: {liveDll}");
        }

        var azureEnv = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["MEMNET_AZURE_STORAGE_SERVICE_URI"] = Environment.GetEnvironmentVariable("MEMNET_AZURE_STORAGE_SERVICE_URI"),
            ["MEMNET_AZURE_DOCUMENTS_CONTAINER"] = Environment.GetEnvironmentVariable("MEMNET_AZURE_DOCUMENTS_CONTAINER"),
            ["MEMNET_AZURE_EVENTS_CONTAINER"] = Environment.GetEnvironmentVariable("MEMNET_AZURE_EVENTS_CONTAINER"),
            ["MEMNET_AZURE_AUDIT_CONTAINER"] = Environment.GetEnvironmentVariable("MEMNET_AZURE_AUDIT_CONTAINER"),
            ["MEMNET_AZURE_SEARCH_ENDPOINT"] = Environment.GetEnvironmentVariable("MEMNET_AZURE_SEARCH_ENDPOINT"),
            ["MEMNET_AZURE_SEARCH_INDEX"] = Environment.GetEnvironmentVariable("MEMNET_AZURE_SEARCH_INDEX"),
            ["MEMNET_AZURE_SEARCH_API_KEY"] = Environment.GetEnvironmentVariable("MEMNET_AZURE_SEARCH_API_KEY"),
            ["MEMNET_AZURE_MANAGED_IDENTITY_CLIENT_ID"] = Environment.GetEnvironmentVariable("MEMNET_AZURE_MANAGED_IDENTITY_CLIENT_ID"),
            ["MEMNET_AZURE_USE_MANAGED_IDENTITY_ONLY"] = Environment.GetEnvironmentVariable("MEMNET_AZURE_USE_MANAGED_IDENTITY_ONLY")
        };

        using var host = await ServiceHost.StartAsync(
            scope.RepoRoot,
            scope.DataRoot,
            scope.ConfigRoot,
            provider: "azure",
            additionalEnvironment: azureEnv,
            serviceDllPath: liveDll);

        using var client = new HttpClient
        {
            BaseAddress = host.BaseAddress,
            Timeout = TimeSpan.FromSeconds(10)
        };

        var health = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        var retentionResponse = await client.PostAsJsonAsync(
            $"/v1/tenants/{scope.Keys.Tenant}/users/{scope.Keys.User}/retention:apply",
            new { policy_id = "project-copilot-v1", as_of_utc = (DateTimeOffset?)null });
        Assert.Equal(HttpStatusCode.OK, retentionResponse.StatusCode);
    }
}

internal static class Assert
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }

    public static void Equal<T>(T expected, T actual)
    {
        if (!Equals(expected, actual))
        {
            throw new Exception($"Expected '{expected}' but got '{actual}'.");
        }
    }

    public static async Task ThrowsAsync<TException>(Func<Task> action, Func<TException, bool> predicate)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException ex)
        {
            if (!predicate(ex))
            {
                throw new Exception($"Exception predicate failed for '{typeof(TException).Name}'.");
            }

            return;
        }

        throw new Exception($"Expected exception '{typeof(TException).Name}' was not thrown.");
    }

    public static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new Exception($"Expected exception '{typeof(TException).Name}' was not thrown.");
    }
}

internal sealed class TestScope : IDisposable
{
    private readonly string _repoRoot;
    private readonly string _dataRoot;
    private readonly string _configRoot;

    public TestScope(
        string repoRoot,
        string dataRoot,
        string configRoot,
        IDocumentStore documentStore,
        MemoryCoordinator coordinator,
        TestKeys keys)
    {
        _repoRoot = repoRoot;
        _dataRoot = dataRoot;
        _configRoot = configRoot;
        DocumentStore = documentStore;
        Coordinator = coordinator;
        Keys = keys;
    }

    public IDocumentStore DocumentStore { get; }

    public MemoryCoordinator Coordinator { get; }

    public TestKeys Keys { get; }

    public string RepoRoot => _repoRoot;

    public string DataRoot => _dataRoot;

    public string ConfigRoot => _configRoot;

    public static TestScope Create()
    {
        var repoRoot = Directory.GetCurrentDirectory();
        var configRoot = Path.Combine(repoRoot, "src", "MemNet.MemoryService", "Policy");
        if (!Directory.Exists(configRoot))
        {
            throw new Exception($"Config directory not found: {configRoot}");
        }

        var dataRoot = Path.Combine(Path.GetTempPath(), "memnet-spec-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);

        var options = new StorageOptions
        {
            DataRoot = dataRoot,
            ConfigRoot = configRoot
        };

        var documentStore = new FileDocumentStore(options);
        var eventStore = new FileEventStore(options);
        var auditStore = new FileAuditStore(options);
        var policy = new PolicyRegistry(options);
        var coordinator = new MemoryCoordinator(
            documentStore,
            eventStore,
            auditStore,
            policy,
            NullLogger<MemoryCoordinator>.Instance);

        var keys = new TestKeys("tenant-1", "user-1");
        SeedDocuments(documentStore, keys).GetAwaiter().GetResult();

        return new TestScope(repoRoot, dataRoot, configRoot, documentStore, coordinator, keys);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dataRoot))
            {
                Directory.Delete(_dataRoot, true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static async Task SeedDocuments(IDocumentStore documentStore, TestKeys keys)
    {
        var now = DateTimeOffset.UtcNow;

        var userProfile = new DocumentEnvelope(
            DocId: Guid.NewGuid().ToString("N"),
            SchemaId: "memory.user.profile",
            SchemaVersion: "1.0.0",
            CreatedAt: now,
            UpdatedAt: now,
            UpdatedBy: "seed",
            Content: new JsonObject
            {
                ["profile"] = new JsonObject
                {
                    ["display_name"] = "Test User",
                    ["locale"] = "en-US"
                },
                ["projects_index"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["project_id"] = "project-alpha",
                        ["aliases"] = new JsonArray("alpha"),
                        ["keywords"] = new JsonArray("retrieval", "latency")
                    }
                }
            });

        var longTermMemory = new DocumentEnvelope(
            DocId: Guid.NewGuid().ToString("N"),
            SchemaId: "memory.user.long_term_memory",
            SchemaVersion: "1.0.0",
            CreatedAt: now,
            UpdatedAt: now,
            UpdatedBy: "seed",
            Content: new JsonObject
            {
                ["preferences"] = new JsonArray("Keep responses concise."),
                ["durable_facts"] = new JsonArray(),
                ["pending_confirmations"] = new JsonArray()
            });

        var projectDoc = new DocumentEnvelope(
            DocId: Guid.NewGuid().ToString("N"),
            SchemaId: "memory.project",
            SchemaVersion: "1.0.0",
            CreatedAt: now,
            UpdatedAt: now,
            UpdatedBy: "seed",
            Content: new JsonObject
            {
                ["summary"] = new JsonArray("Project alpha focuses on retrieval quality."),
                ["facets"] = new JsonObject
                {
                    ["architecture"] = new JsonArray("api", "search")
                },
                ["recent_notes"] = new JsonArray("Tune topK for latency")
            });

        await documentStore.UpsertAsync(keys.UserProfile, userProfile, "*", default);
        await documentStore.UpsertAsync(keys.LongTermMemory, longTermMemory, "*", default);
        await documentStore.UpsertAsync(keys.ProjectAlpha, projectDoc, "*", default);
    }
}

internal sealed class ServiceHost : IDisposable
{
    private readonly Process _process;

    private ServiceHost(Process process, Uri baseAddress)
    {
        _process = process;
        BaseAddress = baseAddress;
    }

    public Uri BaseAddress { get; }

    public static async Task<ServiceHost> StartAsync(
        string repoRoot,
        string dataRoot,
        string configRoot,
        string provider = "filesystem",
        IReadOnlyDictionary<string, string?>? additionalEnvironment = null,
        string? serviceDllPath = null)
    {
        var port = ReserveFreePort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}");
        var baseAddressForHosting = $"http://127.0.0.1:{port}";
        var serviceDll = serviceDllPath;
        if (string.IsNullOrWhiteSpace(serviceDll))
        {
            var candidates = new[]
            {
                Path.Combine(repoRoot, "src", "MemNet.MemoryService", "bin", "Debug", "net8.0", "MemNet.MemoryService.dll"),
                Path.Combine(repoRoot, "src", "MemNet.MemoryService", "bin", "Debug", "azure", "net8.0", "MemNet.MemoryService.dll")
            };
            serviceDll = candidates.FirstOrDefault(File.Exists);
        }

        if (!File.Exists(serviceDll))
        {
            throw new Exception($"Service DLL not found for integration host: {serviceDll}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{serviceDll}\"",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        startInfo.Environment["ASPNETCORE_URLS"] = baseAddressForHosting;
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["MEMNET_DATA_ROOT"] = dataRoot;
        startInfo.Environment["MEMNET_CONFIG_ROOT"] = configRoot;
        startInfo.Environment["MEMNET_PROVIDER"] = provider;

        if (additionalEnvironment is not null)
        {
            foreach (var pair in additionalEnvironment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        var process = new Process { StartInfo = startInfo };
        process.Start();

        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
        var deadline = DateTimeOffset.UtcNow.AddSeconds(12);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                var stderr = await process.StandardError.ReadToEndAsync();
                var stdout = await process.StandardOutput.ReadToEndAsync();
                throw new Exception($"Service process exited early. stdout: {stdout} stderr: {stderr}");
            }

            try
            {
                var response = await client.GetAsync(new Uri(baseAddress, "/"));
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return new ServiceHost(process, baseAddress);
                }
            }
            catch
            {
                // retry until deadline
            }

            await Task.Delay(200);
        }

        process.Kill(true);
        var timeoutStdErr = await process.StandardError.ReadToEndAsync();
        var timeoutStdOut = await process.StandardOutput.ReadToEndAsync();
        throw new Exception($"Service host did not become healthy before timeout. stdout: {timeoutStdOut} stderr: {timeoutStdErr}");
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(true);
                _process.WaitForExit(5000);
            }
        }
        catch
        {
            // best-effort cleanup
        }

        _process.Dispose();
    }

    private static int ReserveFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

internal sealed record TestKeys(string Tenant, string User)
{
    public DocumentKey UserProfile => new(Tenant, User, "user", "profile.json");

    public DocumentKey LongTermMemory => new(Tenant, User, "user", "long_term_memory.json");

    public DocumentKey ProjectAlpha => new(Tenant, User, "projects", "project-alpha.json");
}
