using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MemNet.MemoryService.Core;
using Microsoft.AspNetCore.Http;

namespace MemNet.MemoryService.Infrastructure;

public sealed class FileDocumentStore(StorageOptions options) : IDocumentStore
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public async Task<DocumentRecord?> GetAsync(DocumentKey key, CancellationToken cancellationToken = default)
    {
        var filePath = ResolveDocumentPath(options.DataRoot, key);
        if (!File.Exists(filePath))
        {
            return null;
        }

        var content = await File.ReadAllTextAsync(filePath, cancellationToken);
        var envelope = JsonSerializer.Deserialize<DocumentEnvelope>(content, JsonDefaults.Options)
            ?? throw new ApiException(StatusCodes.Status500InternalServerError, "STORE_DESERIALIZATION_FAILED", "Failed to parse document from storage.");

        return new DocumentRecord(envelope, ComputeETag(content));
    }

    public async Task<DocumentRecord> UpsertAsync(DocumentKey key, DocumentEnvelope envelope, string? ifMatch, CancellationToken cancellationToken = default)
    {
        var filePath = ResolveDocumentPath(options.DataRoot, key);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        var lockKey = filePath;
        var gate = _locks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(filePath))
            {
                var existingContent = await File.ReadAllTextAsync(filePath, cancellationToken);
                var existingETag = ComputeETag(existingContent);
                if (!string.Equals(existingETag, ifMatch, StringComparison.Ordinal))
                {
                    throw new ApiException(
                        StatusCodes.Status412PreconditionFailed,
                        "ETAG_MISMATCH",
                        "If-Match does not match latest document version.",
                        new Dictionary<string, string> { ["latest_etag"] = existingETag });
                }
            }
            else if (!string.IsNullOrWhiteSpace(ifMatch) && ifMatch != "*")
            {
                throw new ApiException(
                    StatusCodes.Status412PreconditionFailed,
                    "ETAG_MISMATCH",
                    "Document does not exist for provided If-Match.");
            }

            var serialized = JsonSerializer.Serialize(envelope, JsonDefaults.Options);
            await File.WriteAllTextAsync(filePath, serialized, cancellationToken);
            var etag = ComputeETag(serialized);

            return new DocumentRecord(envelope, etag);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<bool> ExistsAsync(DocumentKey key, CancellationToken cancellationToken = default)
    {
        var filePath = ResolveDocumentPath(options.DataRoot, key);
        return Task.FromResult(File.Exists(filePath));
    }

    private static string ResolveDocumentPath(string dataRoot, DocumentKey key)
    {
        var safeTenant = SanitizeSegment(key.TenantId);
        var safeUser = SanitizeSegment(key.UserId);
        var safePath = SanitizePath(key.Path);

        return Path.Combine(dataRoot, "tenants", safeTenant, "users", safeUser, "files", safePath);
    }

    private static string SanitizeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "INVALID_PATH_SEGMENT", "Path segment is invalid.");
        }

        return value;
    }

    private static string SanitizePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "INVALID_PATH", "Path must not be empty.");
        }

        var normalized = value.Replace('\\', '/');
        if (normalized.Contains("..", StringComparison.Ordinal))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "INVALID_PATH", "Path must not contain '..'.");
        }

        return normalized;
    }

    public static string ComputeETag(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return $"\"{Convert.ToHexString(bytes)}\"";
    }
}

public sealed class FileEventStore(StorageOptions options) : IEventStore
{
    public async Task WriteAsync(EventDigest digest, CancellationToken cancellationToken = default)
    {
        var filePath = ResolveEventPath(options.DataRoot, digest.TenantId, digest.UserId, digest.EventId);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        var json = JsonSerializer.Serialize(digest, JsonDefaults.Options);
        await File.WriteAllTextAsync(filePath, json, cancellationToken);
    }

    public async Task<IReadOnlyList<EventDigest>> QueryAsync(
        string tenantId,
        string userId,
        EventSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        var dir = ResolveEventsDirectory(options.DataRoot, tenantId, userId);
        if (!Directory.Exists(dir))
        {
            return Array.Empty<EventDigest>();
        }

        var files = Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly);
        var list = new List<(EventDigest Event, double Score)>();
        var queryTokens = Tokenize(request.Query);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = await File.ReadAllTextAsync(file, cancellationToken);
            var item = JsonSerializer.Deserialize<EventDigest>(json, JsonDefaults.Options);
            if (item is null)
            {
                continue;
            }

            if (!PassesFilters(item, request))
            {
                continue;
            }

            var score = Score(item, queryTokens);
            list.Add((item, score));
        }

        var topK = request.TopK <= 0 ? 10 : request.TopK;
        return list
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Event.Timestamp)
            .Take(topK)
            .Select(x => x.Event)
            .ToList();
    }

    private static bool PassesFilters(EventDigest item, EventSearchRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ServiceId) && !string.Equals(item.ServiceId, request.ServiceId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.SourceType) && !string.Equals(item.SourceType, request.SourceType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.ProjectId) && !item.ProjectIds.Contains(request.ProjectId, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (request.From.HasValue && item.Timestamp < request.From.Value)
        {
            return false;
        }

        if (request.To.HasValue && item.Timestamp > request.To.Value)
        {
            return false;
        }

        return true;
    }

    private static double Score(EventDigest item, IReadOnlyList<string> queryTokens)
    {
        if (queryTokens.Count == 0)
        {
            return 0.1;
        }

        var score = 0.0;
        var digestText = item.Digest.ToLowerInvariant();
        foreach (var token in queryTokens)
        {
            if (digestText.Contains(token, StringComparison.Ordinal))
            {
                score += 2.0;
            }

            if (item.Keywords.Any(keyword => keyword.Contains(token, StringComparison.OrdinalIgnoreCase)))
            {
                score += 1.0;
            }
        }

        return score;
    }

    private static IReadOnlyList<string> Tokenize(string? query)
    {
        return string.IsNullOrWhiteSpace(query)
            ? Array.Empty<string>()
            : query
                .ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
    }

    private static string ResolveEventsDirectory(string dataRoot, string tenantId, string userId)
    {
        return Path.Combine(dataRoot, "tenants", tenantId, "users", userId, "events");
    }

    private static string ResolveEventPath(string dataRoot, string tenantId, string userId, string eventId)
    {
        return Path.Combine(ResolveEventsDirectory(dataRoot, tenantId, userId), $"{eventId}.json");
    }
}

public sealed class FileAuditStore(StorageOptions options) : IAuditStore
{
    public async Task WriteAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(
            options.DataRoot,
            "tenants",
            record.TenantId,
            "users",
            record.UserId,
            "audit",
            $"{record.ChangeId}.json");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(record, JsonDefaults.Options);
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }
}

public sealed class FileUserDataMaintenanceStore(StorageOptions options) : IUserDataMaintenanceStore
{
    public Task<ForgetUserResult> ForgetUserAsync(string tenantId, string userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userRoot = ResolveUserRoot(options.DataRoot, tenantId, userId);
        if (!Directory.Exists(userRoot))
        {
            return Task.FromResult(new ForgetUserResult(0, 0, 0, 0, 0));
        }

        var documentsDeleted = CountFiles(Path.Combine(userRoot, "files"), "*.json");
        var eventsDeleted = CountFiles(Path.Combine(userRoot, "events"), "*.json");
        var auditDeleted = CountFiles(Path.Combine(userRoot, "audit"), "*.json");
        var snapshotsDeleted = CountFiles(Path.Combine(userRoot, "snapshots"), "*");

        Directory.Delete(userRoot, recursive: true);
        return Task.FromResult(new ForgetUserResult(documentsDeleted, eventsDeleted, auditDeleted, snapshotsDeleted, 0));
    }

    public async Task<RetentionSweepResult> ApplyRetentionAsync(
        string tenantId,
        string userId,
        RetentionRules rules,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default)
    {
        var userRoot = ResolveUserRoot(options.DataRoot, tenantId, userId);
        if (!Directory.Exists(userRoot))
        {
            return new RetentionSweepResult(
                EventsDeleted: 0,
                AuditDeleted: 0,
                SnapshotsDeleted: 0,
                SearchDocumentsDeleted: 0,
                CutoffEventsUtc: asOfUtc.AddDays(-rules.EventsDays),
                CutoffAuditUtc: asOfUtc.AddDays(-rules.AuditDays),
                CutoffSnapshotsUtc: asOfUtc.AddDays(-rules.SnapshotsDays));
        }

        var eventCutoff = asOfUtc.AddDays(-rules.EventsDays);
        var auditCutoff = asOfUtc.AddDays(-rules.AuditDays);
        var snapshotsCutoff = asOfUtc.AddDays(-rules.SnapshotsDays);

        var eventsDeleted = await DeleteJsonFilesOlderThanAsync<EventDigest>(
            Path.Combine(userRoot, "events"),
            d => d.Timestamp,
            eventCutoff,
            cancellationToken);

        var auditDeleted = await DeleteJsonFilesOlderThanAsync<AuditRecord>(
            Path.Combine(userRoot, "audit"),
            a => a.Timestamp,
            auditCutoff,
            cancellationToken);

        var snapshotsDeleted = DeleteFilesByLastWriteTime(Path.Combine(userRoot, "snapshots"), snapshotsCutoff, cancellationToken);

        return new RetentionSweepResult(
            EventsDeleted: eventsDeleted,
            AuditDeleted: auditDeleted,
            SnapshotsDeleted: snapshotsDeleted,
            SearchDocumentsDeleted: 0,
            CutoffEventsUtc: eventCutoff,
            CutoffAuditUtc: auditCutoff,
            CutoffSnapshotsUtc: snapshotsCutoff);
    }

    private static async Task<int> DeleteJsonFilesOlderThanAsync<TRecord>(
        string directory,
        Func<TRecord, DateTimeOffset> getTimestamp,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            TRecord? record;
            try
            {
                var json = await File.ReadAllTextAsync(file, cancellationToken);
                record = JsonSerializer.Deserialize<TRecord>(json, JsonDefaults.Options);
            }
            catch
            {
                continue;
            }

            if (record is null)
            {
                continue;
            }

            if (getTimestamp(record) >= cutoff)
            {
                continue;
            }

            File.Delete(file);
            deleted++;
        }

        return deleted;
    }

    private static int DeleteFilesByLastWriteTime(string directory, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lastWrite = File.GetLastWriteTimeUtc(file);
            if (lastWrite >= cutoff.UtcDateTime)
            {
                continue;
            }

            File.Delete(file);
            deleted++;
        }

        return deleted;
    }

    private static int CountFiles(string directory, string searchPattern)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        return Directory.EnumerateFiles(directory, searchPattern, SearchOption.AllDirectories).Count();
    }

    private static string ResolveUserRoot(string dataRoot, string tenantId, string userId)
    {
        return Path.Combine(dataRoot, "tenants", SanitizeSegment(tenantId), "users", SanitizeSegment(userId));
    }

    private static string SanitizeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "INVALID_PATH_SEGMENT", "Path segment is invalid.");
        }

        return value;
    }
}
