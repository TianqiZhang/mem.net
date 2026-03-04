using System.Text.Json;
using System.Text.Json.Nodes;
using MemNet.MemoryService.Core;
using MemNet.MemoryService.Infrastructure;

namespace MemNet.MemoryService.UnitTests;

public class RetrievoEventStoreTests : IDisposable
{
    private readonly string _dataRoot;
    private readonly RetrievoEventStore _store;

    public RetrievoEventStoreTests()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), $"memnet-retrievo-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataRoot);
        _store = new RetrievoEventStore(new StorageOptions { DataRoot = _dataRoot });
    }

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAsync_PersistsEventToDisk()
    {
        var digest = CreateDigest("evt-1", "Meeting notes about project alpha");
        await _store.WriteAsync(digest);

        var filePath = Path.Combine(_dataRoot, "tenants", digest.TenantId, "users", digest.UserId, "events", "evt-1.json");
        Assert.True(File.Exists(filePath));

        var json = await File.ReadAllTextAsync(filePath);
        var deserialized = JsonSerializer.Deserialize<EventDigest>(json, JsonDefaults.Options);
        Assert.NotNull(deserialized);
        Assert.Equal("evt-1", deserialized.EventId);
    }

    [Fact]
    public async Task QueryAsync_WithTextQuery_FindsByDigestContent()
    {
        await _store.WriteAsync(CreateDigest("evt-1", "Meeting notes about project alpha"));
        await _store.WriteAsync(CreateDigest("evt-2", "Shopping list for groceries"));
        await _store.WriteAsync(CreateDigest("evt-3", "Alpha team retrospective meeting"));

        var results = await _store.QueryAsync("tenant1", "user1",
            new EventSearchRequest(Query: "meeting", ServiceId: null, SourceType: null, ProjectId: null, From: null, To: null, TopK: 10));

        Assert.True(results.Count >= 1);
        Assert.Contains(results, r => r.EventId == "evt-1");
        Assert.Contains(results, r => r.EventId == "evt-3");
        Assert.DoesNotContain(results, r => r.EventId == "evt-2");
    }

    [Fact]
    public async Task QueryAsync_EmptyQuery_FallsBackToDiskScan()
    {
        await _store.WriteAsync(CreateDigest("evt-1", "First event"));
        await _store.WriteAsync(CreateDigest("evt-2", "Second event"));

        var results = await _store.QueryAsync("tenant1", "user1",
            new EventSearchRequest(Query: null, ServiceId: null, SourceType: null, ProjectId: null, From: null, To: null, TopK: 10));

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task QueryAsync_EmptyQuery_RespectsTopK()
    {
        for (var i = 0; i < 5; i++)
        {
            await _store.WriteAsync(CreateDigest($"evt-{i}", $"Event number {i}",
                timestamp: DateTimeOffset.UtcNow.AddMinutes(-i)));
        }

        var results = await _store.QueryAsync("tenant1", "user1",
            new EventSearchRequest(Query: null, ServiceId: null, SourceType: null, ProjectId: null, From: null, To: null, TopK: 3));

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task QueryAsync_FiltersByServiceId()
    {
        await _store.WriteAsync(CreateDigest("evt-1", "Alpha event", serviceId: "svc-a"));
        await _store.WriteAsync(CreateDigest("evt-2", "Beta event", serviceId: "svc-b"));

        var results = await _store.QueryAsync("tenant1", "user1",
            new EventSearchRequest(Query: "event", ServiceId: "svc-a", SourceType: null, ProjectId: null, From: null, To: null, TopK: 10));

        Assert.Single(results);
        Assert.Equal("evt-1", results[0].EventId);
    }

    [Fact]
    public async Task QueryAsync_FiltersBySourceType()
    {
        await _store.WriteAsync(CreateDigest("evt-1", "Alpha event", sourceType: "chat"));
        await _store.WriteAsync(CreateDigest("evt-2", "Beta event", sourceType: "email"));

        var results = await _store.QueryAsync("tenant1", "user1",
            new EventSearchRequest(Query: "event", ServiceId: null, SourceType: "chat", ProjectId: null, From: null, To: null, TopK: 10));

        Assert.Single(results);
        Assert.Equal("evt-1", results[0].EventId);
    }

    [Fact]
    public async Task QueryAsync_FiltersByProjectId_UsingContainsFilter()
    {
        await _store.WriteAsync(CreateDigest("evt-1", "Alpha event", projectIds: ["proj-a", "proj-b"]));
        await _store.WriteAsync(CreateDigest("evt-2", "Beta event", projectIds: ["proj-c"]));

        var results = await _store.QueryAsync("tenant1", "user1",
            new EventSearchRequest(Query: "event", ServiceId: null, SourceType: null, ProjectId: "proj-b", From: null, To: null, TopK: 10));

        Assert.Single(results);
        Assert.Equal("evt-1", results[0].EventId);
    }

    [Fact]
    public async Task QueryAsync_FiltersByDateRange()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.WriteAsync(CreateDigest("evt-old", "Old event", timestamp: now.AddDays(-10)));
        await _store.WriteAsync(CreateDigest("evt-recent", "Recent event", timestamp: now.AddDays(-1)));
        await _store.WriteAsync(CreateDigest("evt-future", "Future event", timestamp: now.AddDays(1)));

        var results = await _store.QueryAsync("tenant1", "user1",
            new EventSearchRequest(Query: "event", ServiceId: null, SourceType: null, ProjectId: null,
                From: now.AddDays(-5), To: now, TopK: 10));

        Assert.Single(results);
        Assert.Equal("evt-recent", results[0].EventId);
    }

    [Fact]
    public async Task QueryAsync_EmptyQuery_FiltersByServiceId()
    {
        await _store.WriteAsync(CreateDigest("evt-1", "Alpha event", serviceId: "svc-a"));
        await _store.WriteAsync(CreateDigest("evt-2", "Beta event", serviceId: "svc-b"));

        var results = await _store.QueryAsync("tenant1", "user1",
            new EventSearchRequest(Query: null, ServiceId: "svc-a", SourceType: null, ProjectId: null, From: null, To: null, TopK: 10));

        Assert.Single(results);
        Assert.Equal("evt-1", results[0].EventId);
    }

    [Fact]
    public async Task QueryAsync_EmptyQuery_FiltersByDateRange()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.WriteAsync(CreateDigest("evt-old", "Old event", timestamp: now.AddDays(-10)));
        await _store.WriteAsync(CreateDigest("evt-recent", "Recent event", timestamp: now.AddDays(-1)));

        var results = await _store.QueryAsync("tenant1", "user1",
            new EventSearchRequest(Query: null, ServiceId: null, SourceType: null, ProjectId: null,
                From: now.AddDays(-5), To: now, TopK: 10));

        Assert.Single(results);
        Assert.Equal("evt-recent", results[0].EventId);
    }

    [Fact]
    public async Task QueryAsync_RespectsTopK()
    {
        for (var i = 0; i < 10; i++)
        {
            await _store.WriteAsync(CreateDigest($"evt-{i}", $"Common search term event {i}"));
        }

        var results = await _store.QueryAsync("tenant1", "user1",
            new EventSearchRequest(Query: "common search term", ServiceId: null, SourceType: null, ProjectId: null, From: null, To: null, TopK: 3));

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task QueryAsync_WrongTenantOrUser_ReturnsEmpty()
    {
        await _store.WriteAsync(CreateDigest("evt-1", "Meeting notes"));

        var results = await _store.QueryAsync("other-tenant", "user1",
            new EventSearchRequest(Query: "meeting", ServiceId: null, SourceType: null, ProjectId: null, From: null, To: null, TopK: 10));

        Assert.Empty(results);
    }

    [Fact]
    public async Task Constructor_IndexesExistingEventsFromDisk()
    {
        // Write events directly to disk (not through the store)
        var digest = CreateDigest("evt-preexist", "Pre-existing meeting notes");
        var eventsDir = Path.Combine(_dataRoot, "tenants", "tenant1", "users", "user1", "events");
        Directory.CreateDirectory(eventsDir);
        var json = JsonSerializer.Serialize(digest, JsonDefaults.Options);
        await File.WriteAllTextAsync(Path.Combine(eventsDir, "evt-preexist.json"), json);

        // Create a new store instance — it should index existing files
        using var newStore = new RetrievoEventStore(new StorageOptions { DataRoot = _dataRoot });

        var results = await newStore.QueryAsync("tenant1", "user1",
            new EventSearchRequest(Query: "meeting", ServiceId: null, SourceType: null, ProjectId: null, From: null, To: null, TopK: 10));

        Assert.Single(results);
        Assert.Equal("evt-preexist", results[0].EventId);
    }

    [Fact]
    public async Task QueryAsync_NoMatchingResults_ReturnsEmpty()
    {
        await _store.WriteAsync(CreateDigest("evt-1", "Meeting notes about project alpha"));

        var results = await _store.QueryAsync("tenant1", "user1",
            new EventSearchRequest(Query: "nonexistent gibberish xyz", ServiceId: null, SourceType: null, ProjectId: null, From: null, To: null, TopK: 10));

        Assert.Empty(results);
    }

    [Fact]
    public void MemoryBackendFactory_CreatesRetrievoBackend()
    {
        var backend = MemoryBackendFactory.Create("retrievo");
        Assert.IsType<RetrievoMemoryBackend>(backend);
        Assert.Equal("retrievo", backend.Name);
    }

    [Fact]
    public void MemoryBackendFactory_UnknownProvider_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => MemoryBackendFactory.Create("unknown"));
    }

    [Fact]
    public async Task QueryAsync_CrossTenantSameEventId_NoDataLeak()
    {
        // Two tenants with the same EventId should not collide
        var digest1 = CreateDigest("shared-id", "Tenant one secret notes", tenantId: "tenantA", userId: "user1");
        var digest2 = CreateDigest("shared-id", "Tenant two private data", tenantId: "tenantB", userId: "user1");

        await _store.WriteAsync(digest1);
        await _store.WriteAsync(digest2);

        var resultsA = await _store.QueryAsync("tenantA", "user1",
            new EventSearchRequest(Query: "notes", ServiceId: null, SourceType: null, ProjectId: null, From: null, To: null, TopK: 10));
        var resultsB = await _store.QueryAsync("tenantB", "user1",
            new EventSearchRequest(Query: "data", ServiceId: null, SourceType: null, ProjectId: null, From: null, To: null, TopK: 10));

        Assert.Single(resultsA);
        Assert.Equal("Tenant one secret notes", resultsA[0].Digest);
        Assert.Single(resultsB);
        Assert.Equal("Tenant two private data", resultsB[0].Digest);
    }

    [Fact]
    public async Task QueryAsync_TextQuery_FiltersCaseInsensitively()
    {
        // ServiceId stored as "SVC-A" should match filter "svc-a" in both text and empty query paths
        await _store.WriteAsync(CreateDigest("evt-1", "Important meeting notes", serviceId: "SVC-A"));
        await _store.WriteAsync(CreateDigest("evt-2", "Other meeting notes", serviceId: "svc-b"));

        // Text query path (through Retrievo)
        var textResults = await _store.QueryAsync("tenant1", "user1",
            new EventSearchRequest(Query: "meeting", ServiceId: "svc-a", SourceType: null, ProjectId: null, From: null, To: null, TopK: 10));

        // Empty query path (fallback disk scan)
        var emptyResults = await _store.QueryAsync("tenant1", "user1",
            new EventSearchRequest(Query: null, ServiceId: "svc-a", SourceType: null, ProjectId: null, From: null, To: null, TopK: 10));

        Assert.Single(textResults);
        Assert.Equal("evt-1", textResults[0].EventId);
        Assert.Single(emptyResults);
        Assert.Equal("evt-1", emptyResults[0].EventId);
    }

    private static EventDigest CreateDigest(
        string eventId,
        string digestText,
        string serviceId = "test-service",
        string sourceType = "test",
        IReadOnlyList<string>? projectIds = null,
        DateTimeOffset? timestamp = null,
        string tenantId = "tenant1",
        string userId = "user1")
    {
        return new EventDigest(
            EventId: eventId,
            TenantId: tenantId,
            UserId: userId,
            ServiceId: serviceId,
            Timestamp: timestamp ?? DateTimeOffset.UtcNow,
            SourceType: sourceType,
            Digest: digestText,
            Keywords: ["test", "keyword"],
            ProjectIds: projectIds ?? ["default-project"],
            Evidence: null);
}
}
