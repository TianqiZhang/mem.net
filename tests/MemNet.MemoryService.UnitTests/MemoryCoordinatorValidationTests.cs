using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using MemNet.MemoryService.Core;
using MemNet.MemoryService.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace MemNet.MemoryService.UnitTests;

public class MemoryCoordinatorValidationTests
{
    [Fact]
    public async Task PatchDocument_MissingIfMatch_Returns400()
    {
        var store = new FakeDocumentStore();
        var key = TestKey;
        await store.UpsertAsync(key, CreateEnvelope(new JsonObject { ["value"] = "seed" }), "*");
        var coordinator = CreateCoordinator(store);

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => coordinator.PatchDocumentAsync(
                key,
                new PatchDocumentRequest(
                    Ops: [new PatchOperation("replace", "/content/value", JsonValue.Create("updated"))],
                    Reason: "test",
                    Evidence: null),
                ifMatch: "",
                actor: "tests"));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("MISSING_IF_MATCH", ex.Code);
    }

    [Fact]
    public async Task PatchDocument_WithoutOpsAndEdits_Returns400()
    {
        var store = new FakeDocumentStore();
        var key = TestKey;
        var seeded = await store.UpsertAsync(key, CreateEnvelope(new JsonObject { ["value"] = "seed" }), "*");
        var coordinator = CreateCoordinator(store);

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => coordinator.PatchDocumentAsync(
                key,
                new PatchDocumentRequest(
                    Ops: [],
                    Reason: "test",
                    Evidence: null,
                    Edits: []),
                ifMatch: seeded.ETag,
                actor: "tests"));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("INVALID_PATCH", ex.Code);
    }

    [Fact]
    public async Task PatchDocument_TextEdits_AmbiguousMatch_Returns422()
    {
        var store = new FakeDocumentStore();
        var key = TestKey;
        var seeded = await store.UpsertAsync(
            key,
            CreateEnvelope(new JsonObject
            {
                ["text"] = "alpha\nalpha\n"
            }),
            "*");
        var coordinator = CreateCoordinator(store);

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => coordinator.PatchDocumentAsync(
                key,
                new PatchDocumentRequest(
                    Ops: [],
                    Reason: "text_patch",
                    Evidence: null,
                    Edits: [new TextPatchEdit("alpha\n", "beta\n", null)]),
                ifMatch: seeded.ETag,
                actor: "tests"));

        Assert.Equal(422, ex.StatusCode);
        Assert.Equal("PATCH_MATCH_AMBIGUOUS", ex.Code);
    }

    [Fact]
    public async Task PatchDocument_TextEdits_ApplyDeterministicallyWithOccurrence()
    {
        var store = new FakeDocumentStore();
        var key = TestKey;
        var seeded = await store.UpsertAsync(
            key,
            CreateEnvelope(new JsonObject
            {
                ["text"] = "line A\nline A\nline B\n"
            }),
            "*");
        var coordinator = CreateCoordinator(store);

        var patched = await coordinator.PatchDocumentAsync(
            key,
            new PatchDocumentRequest(
                Ops: [],
                Reason: "text_patch",
                Evidence: null,
                Edits: [new TextPatchEdit("line A\n", "line X\n", 2)]),
            ifMatch: seeded.ETag,
            actor: "tests");

        Assert.Equal("line A\nline X\nline B\n", patched.Document.Content["text"]?.GetValue<string>());
    }

    [Fact]
    public async Task AssembleContext_EmptyFiles_Returns400()
    {
        var coordinator = CreateCoordinator(new FakeDocumentStore());

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => coordinator.AssembleContextAsync(
                "tenant",
                "user",
                new AssembleContextRequest(Files: [], MaxDocs: 4, MaxCharsTotal: 3000)));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("MISSING_ASSEMBLY_TARGETS", ex.Code);
    }

    [Fact]
    public async Task ListFiles_InvalidLimit_Returns400()
    {
        var coordinator = CreateCoordinator(new FakeDocumentStore());

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => coordinator.ListFilesAsync(
                "tenant",
                "user",
                new ListFilesRequest(Prefix: "projects/", Limit: 0)));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("INVALID_LIMIT", ex.Code);
    }

    private static MemoryCoordinator CreateCoordinator(IDocumentStore documentStore)
    {
        return new MemoryCoordinator(
            documentStore,
            new FakeEventStore(),
            new FakeAuditStore(),
            NullLogger<MemoryCoordinator>.Instance);
    }

    private static DocumentEnvelope CreateEnvelope(JsonObject content)
    {
        var now = DateTimeOffset.UtcNow;
        return new DocumentEnvelope(
            DocId: $"doc-{Guid.NewGuid():N}",
            SchemaId: "memnet.file",
            SchemaVersion: "1.0.0",
            CreatedAt: now,
            UpdatedAt: now,
            UpdatedBy: "seed",
            Content: content);
    }

    private static readonly DocumentKey TestKey = new("tenant", "user", "user/test.json");

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

        public Task<IReadOnlyList<FileListItem>> ListAsync(
            string tenantId,
            string userId,
            string? prefix,
            int limit,
            CancellationToken cancellationToken = default)
        {
            _ = tenantId;
            _ = userId;
            _ = cancellationToken;

            var normalizedPrefix = string.IsNullOrWhiteSpace(prefix)
                ? null
                : prefix.Replace('\\', '/').Trim().TrimStart('/');

            var files = _records.Keys
                .Select(x => x[(x.IndexOf('/', x.IndexOf('/') + 1) + 1)..])
                .Where(x => normalizedPrefix is null || x.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(x => new FileListItem(x, DateTimeOffset.UtcNow))
                .ToArray();

            return Task.FromResult<IReadOnlyList<FileListItem>>(files);
        }

        public Task<bool> ExistsAsync(DocumentKey key, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_records.ContainsKey(Key(key)));
        }

        private static string Key(DocumentKey key) => $"{key.TenantId}/{key.UserId}/{key.Path}";
    }

    private sealed class FakeEventStore : IEventStore
    {
        public Task WriteAsync(EventDigest digest, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<EventDigest>> QueryAsync(
            string tenantId,
            string userId,
            EventSearchRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EventDigest>>(Array.Empty<EventDigest>());
    }

    private sealed class FakeAuditStore : IAuditStore
    {
        public Task WriteAsync(AuditRecord record, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
