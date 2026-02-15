using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MemNet.MemoryService.Core;
using Microsoft.AspNetCore.Http;

namespace MemNet.MemoryService.Infrastructure;

public sealed class StorageOptions
{
    public required string DataRoot { get; init; }

    public required string ConfigRoot { get; init; }
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };
}

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
        var safeNamespace = SanitizeSegment(key.Namespace);
        var safePath = SanitizePath(key.Path);

        return Path.Combine(dataRoot, "tenants", safeTenant, "users", safeUser, "documents", safeNamespace, safePath);
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

public sealed class FileRegistryProvider : IProfileRegistryProvider, ISchemaRegistryProvider
{
    private readonly Dictionary<string, ProfileConfig> _profiles;
    private readonly Dictionary<(string SchemaId, string Version), SchemaConfig> _schemas;

    public FileRegistryProvider(StorageOptions options)
    {
        var schemasPath = Path.Combine(options.ConfigRoot, "schemas.json");
        var profilesPath = Path.Combine(options.ConfigRoot, "profiles.json");

        var schemaRegistry = JsonSerializer.Deserialize<SchemaRegistry>(File.ReadAllText(schemasPath), JsonDefaults.Options)
            ?? throw new InvalidOperationException("Failed to load schema registry.");
        var profileRegistry = JsonSerializer.Deserialize<ProfileRegistry>(File.ReadAllText(profilesPath), JsonDefaults.Options)
            ?? throw new InvalidOperationException("Failed to load profile registry.");

        _schemas = schemaRegistry.Schemas.ToDictionary(x => (x.SchemaId, x.Version), x => x);
        _profiles = profileRegistry.Profiles.ToDictionary(x => x.ProfileId, x => x, StringComparer.Ordinal);
    }

    public ProfileConfig GetProfile(string profileId)
    {
        if (_profiles.TryGetValue(profileId, out var profile))
        {
            return profile;
        }

        throw new ApiException(StatusCodes.Status404NotFound, "PROFILE_NOT_FOUND", $"Profile '{profileId}' was not found.");
    }

    public SchemaConfig GetSchema(string schemaId, string version)
    {
        if (_schemas.TryGetValue((schemaId, version), out var schema))
        {
            return schema;
        }

        throw new ApiException(StatusCodes.Status422UnprocessableEntity, "SCHEMA_NOT_FOUND", $"Schema '{schemaId}:{version}' was not found.");
    }
}

public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private sealed record Entry(string PayloadHash, MutationResponse? Response);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public IdempotencyResult Begin(string key, string payloadHash)
    {
        while (true)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                if (!string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal))
                {
                    return new IdempotencyResult(IdempotencyState.Conflict, null, existing.PayloadHash);
                }

                return new IdempotencyResult(IdempotencyState.Replayed, existing.Response, existing.PayloadHash);
            }

            if (_entries.TryAdd(key, new Entry(payloadHash, null)))
            {
                return new IdempotencyResult(IdempotencyState.Started, null, null);
            }
        }
    }

    public void Complete(string key, string payloadHash, MutationResponse response)
    {
        _entries[key] = new Entry(payloadHash, response);
    }
}
