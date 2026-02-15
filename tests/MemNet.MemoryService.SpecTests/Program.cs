using System.Text.Json.Nodes;
using MemNet.MemoryService.Core;
using MemNet.MemoryService.Infrastructure;

var runner = new SpecRunner();
await runner.RunAsync();

internal sealed class SpecRunner
{
    private readonly List<Func<Task>> _tests;

    public SpecRunner()
    {
        _tests =
        [
            PatchDocumentHappyPathAsync,
            PatchDocumentReturns412OnEtagMismatchAsync,
            PatchDocumentReturns409OnIdempotencyConflictAsync,
            PatchDocumentReturns422OnPathPolicyViolationAsync,
            PatchDocumentReturns422OnLowConfidenceDurableFactsAsync,
            AssembleContextRoutesProjectAndRespectsBudgetsAsync,
            EventSearchReturnsRelevantResultsAsync
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

    private static async Task PatchDocumentHappyPathAsync()
    {
        using var scope = TestScope.Create();
        var key = scope.Keys.UserDynamic;
        var seeded = await scope.DocumentStore.GetAsync(key) ?? throw new Exception("Seeded user_dynamic missing.");

        var result = await scope.Coordinator.PatchDocumentAsync(
            key,
            new PatchDocumentRequest(
                ProfileId: "project-copilot-v1",
                BindingId: "user_dynamic",
                Ops:
                [
                    new PatchOperation("replace", "/content/preferences/0", JsonValue.Create("Use concise answers with examples."))
                ],
                Reason: "live_update",
                Evidence: new EvidenceRef("conv1", ["m1"], null),
                Confidence: 0.9),
            ifMatch: seeded.ETag,
            idempotencyKey: "idem-happy",
            actor: "spec-tests");

        Assert.True(result.ETag != seeded.ETag, "ETag should change after patch.");
        Assert.Equal("Use concise answers with examples.", result.Document.Content["preferences"]?[0]?.GetValue<string>());
        Assert.Equal("spec-tests", result.Document.UpdatedBy);
    }

    private static async Task PatchDocumentReturns412OnEtagMismatchAsync()
    {
        using var scope = TestScope.Create();
        var key = scope.Keys.UserDynamic;

        await Assert.ThrowsAsync<ApiException>(
            async () =>
            {
                await scope.Coordinator.PatchDocumentAsync(
                    key,
                    new PatchDocumentRequest(
                        ProfileId: "project-copilot-v1",
                        BindingId: "user_dynamic",
                        Ops:
                        [
                            new PatchOperation("replace", "/content/preferences/0", JsonValue.Create("Mismatch test"))
                        ],
                        Reason: "live_update",
                        Evidence: null,
                        Confidence: 0.9),
                    ifMatch: "\"stale\"",
                    idempotencyKey: "idem-etag",
                    actor: "spec-tests");
            },
            ex => ex.StatusCode == 412 && ex.Code == "ETAG_MISMATCH");
    }

    private static async Task PatchDocumentReturns409OnIdempotencyConflictAsync()
    {
        using var scope = TestScope.Create();
        var key = scope.Keys.UserDynamic;
        var seeded = await scope.DocumentStore.GetAsync(key) ?? throw new Exception("Seeded user_dynamic missing.");

        await scope.Coordinator.PatchDocumentAsync(
            key,
            new PatchDocumentRequest(
                ProfileId: "project-copilot-v1",
                BindingId: "user_dynamic",
                Ops:
                [
                    new PatchOperation("replace", "/content/preferences/0", JsonValue.Create("Version A"))
                ],
                Reason: "live_update",
                Evidence: null,
                Confidence: 0.9),
            ifMatch: seeded.ETag,
            idempotencyKey: "idem-conflict",
            actor: "spec-tests");

        var latest = await scope.DocumentStore.GetAsync(key) ?? throw new Exception("Updated user_dynamic missing.");

        await Assert.ThrowsAsync<ApiException>(
            async () =>
            {
                await scope.Coordinator.PatchDocumentAsync(
                    key,
                    new PatchDocumentRequest(
                        ProfileId: "project-copilot-v1",
                        BindingId: "user_dynamic",
                        Ops:
                        [
                            new PatchOperation("replace", "/content/preferences/0", JsonValue.Create("Version B"))
                        ],
                        Reason: "live_update",
                        Evidence: null,
                        Confidence: 0.9),
                    ifMatch: latest.ETag,
                    idempotencyKey: "idem-conflict",
                    actor: "spec-tests");
            },
            ex => ex.StatusCode == 409 && ex.Code == "IDEMPOTENCY_CONFLICT");
    }

    private static async Task PatchDocumentReturns422OnPathPolicyViolationAsync()
    {
        using var scope = TestScope.Create();
        var key = scope.Keys.UserDynamic;
        var seeded = await scope.DocumentStore.GetAsync(key) ?? throw new Exception("Seeded user_dynamic missing.");

        await Assert.ThrowsAsync<ApiException>(
            async () =>
            {
                await scope.Coordinator.PatchDocumentAsync(
                    key,
                    new PatchDocumentRequest(
                        ProfileId: "project-copilot-v1",
                        BindingId: "user_dynamic",
                        Ops:
                        [
                            new PatchOperation("replace", "/content/profile/display_name", JsonValue.Create("Oops"))
                        ],
                        Reason: "live_update",
                        Evidence: null,
                        Confidence: 0.9),
                    ifMatch: seeded.ETag,
                    idempotencyKey: "idem-path-policy",
                    actor: "spec-tests");
            },
            ex => ex.StatusCode == 422 && ex.Code == "PATH_NOT_WRITABLE");
    }

    private static async Task PatchDocumentReturns422OnLowConfidenceDurableFactsAsync()
    {
        using var scope = TestScope.Create();
        var key = scope.Keys.UserDynamic;
        var seeded = await scope.DocumentStore.GetAsync(key) ?? throw new Exception("Seeded user_dynamic missing.");

        await Assert.ThrowsAsync<ApiException>(
            async () =>
            {
                await scope.Coordinator.PatchDocumentAsync(
                    key,
                    new PatchDocumentRequest(
                        ProfileId: "project-copilot-v1",
                        BindingId: "user_dynamic",
                        Ops:
                        [
                            new PatchOperation("add", "/content/durable_facts/-", JsonValue.Create("User prefers concise architecture docs."))
                        ],
                        Reason: "replay_update",
                        Evidence: null,
                        Confidence: 0.2),
                    ifMatch: seeded.ETag,
                    idempotencyKey: "idem-confidence",
                    actor: "spec-tests");
            },
            ex => ex.StatusCode == 422 && ex.Code == "CONFIDENCE_TOO_LOW");
    }

    private static async Task AssembleContextRoutesProjectAndRespectsBudgetsAsync()
    {
        using var scope = TestScope.Create();

        var full = await scope.Coordinator.AssembleContextAsync(
            tenantId: scope.Keys.Tenant,
            userId: scope.Keys.User,
            request: new AssembleContextRequest(
                ProfileId: "project-copilot-v1",
                ConversationHint: new ConversationHint("Need help with alpha retrieval", null),
                MaxDocs: 5,
                MaxCharsTotal: 30000));

        Assert.Equal("project-alpha", full.SelectedProjectId);
        Assert.True(full.Documents.Count >= 3, "Expected assembled context to include user_static, user_dynamic, and project doc.");

        var tinyBudget = await scope.Coordinator.AssembleContextAsync(
            tenantId: scope.Keys.Tenant,
            userId: scope.Keys.User,
            request: new AssembleContextRequest(
                ProfileId: "project-copilot-v1",
                ConversationHint: new ConversationHint("Need help with alpha retrieval", null),
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
}

internal sealed class TestScope : IDisposable
{
    private readonly string _dataRoot;

    public TestScope(
        string dataRoot,
        IDocumentStore documentStore,
        MemoryCoordinator coordinator,
        TestKeys keys)
    {
        _dataRoot = dataRoot;
        DocumentStore = documentStore;
        Coordinator = coordinator;
        Keys = keys;
    }

    public IDocumentStore DocumentStore { get; }

    public MemoryCoordinator Coordinator { get; }

    public TestKeys Keys { get; }

    public static TestScope Create()
    {
        var repoRoot = Directory.GetCurrentDirectory();
        var configRoot = Path.Combine(repoRoot, "src", "MemNet.MemoryService", "config");
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
        var registry = new FileRegistryProvider(options);
        var idempotency = new InMemoryIdempotencyStore();
        var coordinator = new MemoryCoordinator(documentStore, eventStore, auditStore, registry, registry, idempotency);

        var keys = new TestKeys("tenant-1", "user-1");
        SeedDocuments(documentStore, keys).GetAwaiter().GetResult();

        return new TestScope(dataRoot, documentStore, coordinator, keys);
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

        var userStatic = new DocumentEnvelope(
            DocId: Guid.NewGuid().ToString("N"),
            SchemaId: "memory.user.static",
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
                }
            });

        var userDynamic = new DocumentEnvelope(
            DocId: Guid.NewGuid().ToString("N"),
            SchemaId: "memory.user.dynamic",
            SchemaVersion: "1.0.0",
            CreatedAt: now,
            UpdatedAt: now,
            UpdatedBy: "seed",
            Content: new JsonObject
            {
                ["preferences"] = new JsonArray("Keep responses concise."),
                ["durable_facts"] = new JsonArray(),
                ["pending_confirmations"] = new JsonArray(),
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

        await documentStore.UpsertAsync(keys.UserStatic, userStatic, "*", default);
        await documentStore.UpsertAsync(keys.UserDynamic, userDynamic, "*", default);
        await documentStore.UpsertAsync(keys.ProjectAlpha, projectDoc, "*", default);
    }
}

internal sealed record TestKeys(string Tenant, string User)
{
    public DocumentKey UserStatic => new(Tenant, User, "user", "user_static.json");

    public DocumentKey UserDynamic => new(Tenant, User, "user", "user_dynamic.json");

    public DocumentKey ProjectAlpha => new(Tenant, User, "projects", "project-alpha.json");
}
