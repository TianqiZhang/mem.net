using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using MemNet.MemoryService.Core;
using MemNet.MemoryService.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace MemNet.MemoryService.UnitTests;

public class LifecycleAndReplayServiceTests
{
    [Fact]
    public async Task ApplyRetention_NegativeEventsDays_Returns400()
    {
        var service = new DataLifecycleService(new FakeMaintenanceStore());

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => service.ApplyRetentionAsync(
                "tenant",
                "user",
                new ApplyRetentionRequest(
                    EventsDays: -1,
                    AuditDays: 30,
                    SnapshotsDays: 30,
                    AsOfUtc: DateTimeOffset.UtcNow)));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("INVALID_RETENTION_VALUE", ex.Code);
    }

    [Fact]
    public async Task ApplyRetention_UsesUtcAsOfAndForwardsRules()
    {
        var store = new FakeMaintenanceStore();
        var service = new DataLifecycleService(store);
        var asOfWithOffset = new DateTimeOffset(2026, 1, 2, 8, 30, 0, TimeSpan.FromHours(5));

        await service.ApplyRetentionAsync(
            "tenant-a",
            "user-a",
            new ApplyRetentionRequest(
                EventsDays: 365,
                AuditDays: 90,
                SnapshotsDays: 30,
                AsOfUtc: asOfWithOffset));

        Assert.Equal("tenant-a", store.LastTenantId);
        Assert.Equal("user-a", store.LastUserId);
        Assert.Equal(new RetentionRules(30, 365, 90), store.LastRules);
        Assert.Equal(asOfWithOffset.ToUniversalTime(), store.LastAsOfUtc);
    }

    [Fact]
    public async Task ForgetUser_ForwardsIdentifiersToStore()
    {
        var store = new FakeMaintenanceStore();
        var service = new DataLifecycleService(store);

        var result = await service.ForgetUserAsync("tenant-z", "user-z");

        Assert.Equal("tenant-z", store.LastTenantId);
        Assert.Equal("user-z", store.LastUserId);
        Assert.Equal(3, result.DocumentsDeleted);
    }

    [Fact]
    public async Task ApplyReplayPatchAsync_UsesReplayPayloadAndReplayEtag()
    {
        var documentStore = new FakeDocumentStore();
        var auditStore = new RecordingAuditStore();
        var coordinator = new MemoryCoordinator(
            documentStore,
            new FakeEventStore(),
            auditStore,
            NullLogger<MemoryCoordinator>.Instance);

        var key = new DocumentKey("tenant", "user", "user/profile.json");
        var seeded = await documentStore.UpsertAsync(key, CreateEnvelope("before"), "*");

        var replay = new ReplayPatchRecord(
            ReplayId: "rpl_1",
            TargetBindingId: "binding_1",
            TargetPath: key.Path,
            BaseETag: seeded.ETag,
            Ops:
            [
                new PatchOperation("replace", "/content/text", JsonValue.Create("after"))
            ],
            Evidence: new JsonObject { ["source"] = "replay" });

        var replayService = new ReplayService(coordinator);
        var response = await replayService.ApplyReplayPatchAsync(key, replay, actor: "replay-agent");

        Assert.Equal("after", response.Document.Content["text"]?.GetValue<string>());
        Assert.Single(auditStore.Records);
        Assert.Equal("replay_update", auditStore.Records[0].Reason);
        Assert.Equal(seeded.ETag, auditStore.Records[0].PreviousETag);
    }

    private static DocumentEnvelope CreateEnvelope(string text)
    {
        var now = DateTimeOffset.UtcNow;
        return new DocumentEnvelope(
            DocId: $"doc-{Guid.NewGuid():N}",
            SchemaId: "memnet.file",
            SchemaVersion: "1.0.0",
            CreatedAt: now,
            UpdatedAt: now,
            UpdatedBy: "seed",
            Content: new JsonObject { ["text"] = text });
    }

    private sealed class FakeMaintenanceStore : IUserDataMaintenanceStore
    {
        public string? LastTenantId { get; private set; }
        public string? LastUserId { get; private set; }
        public RetentionRules? LastRules { get; private set; }
        public DateTimeOffset? LastAsOfUtc { get; private set; }

        public Task<ForgetUserResult> ForgetUserAsync(string tenantId, string userId, CancellationToken cancellationToken = default)
        {
            LastTenantId = tenantId;
            LastUserId = userId;
            return Task.FromResult(new ForgetUserResult(3, 2, 1, 0, 0));
        }

        public Task<RetentionSweepResult> ApplyRetentionAsync(
            string tenantId,
            string userId,
            RetentionRules rules,
            DateTimeOffset asOfUtc,
            CancellationToken cancellationToken = default)
        {
            LastTenantId = tenantId;
            LastUserId = userId;
            LastRules = rules;
            LastAsOfUtc = asOfUtc;

            return Task.FromResult(new RetentionSweepResult(1, 1, 1, 0, asOfUtc, asOfUtc, asOfUtc));
        }
    }

    private sealed class FakeDocumentStore : IDocumentStore
    {
        private readonly ConcurrentDictionary<string, DocumentRecord> _records = new(StringComparer.Ordinal);
        private int _version;

        public Task<DocumentRecord?> GetAsync(DocumentKey key, CancellationToken cancellationToken = default)
        {
            _records.TryGetValue(Key(key), out var record);
            return Task.FromResult(record);
        }

        public Task<DocumentRecord> UpsertAsync(DocumentKey key, DocumentEnvelope envelope, string? ifMatch, CancellationToken cancellationToken = default)
        {
            var id = Key(key);
            if (_records.TryGetValue(id, out var existing))
            {
                if (!string.Equals(existing.ETag, ifMatch, StringComparison.Ordinal))
                {
                    throw new ApiException(412, "ETAG_MISMATCH", "stale");
                }
            }
            else if (!string.IsNullOrWhiteSpace(ifMatch) && ifMatch != "*")
            {
                throw new ApiException(412, "ETAG_MISMATCH", "missing");
            }

            var etag = $"\"v{Interlocked.Increment(ref _version)}\"";
            var stored = new DocumentRecord(envelope, etag);
            _records[id] = stored;
            return Task.FromResult(stored);
        }

        public Task<IReadOnlyList<FileListItem>> ListAsync(string tenantId, string userId, string? prefix, int limit, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<FileListItem>>(Array.Empty<FileListItem>());

        public Task<bool> ExistsAsync(DocumentKey key, CancellationToken cancellationToken = default)
            => Task.FromResult(_records.ContainsKey(Key(key)));

        private static string Key(DocumentKey key) => $"{key.TenantId}/{key.UserId}/{key.Path}";
    }

    private sealed class FakeEventStore : IEventStore
    {
        public Task WriteAsync(EventDigest digest, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<EventDigest>> QueryAsync(string tenantId, string userId, EventSearchRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<EventDigest>>(Array.Empty<EventDigest>());
    }

    private sealed class RecordingAuditStore : IAuditStore
    {
        public List<AuditRecord> Records { get; } = [];

        public Task WriteAsync(AuditRecord record, CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }
    }
}
