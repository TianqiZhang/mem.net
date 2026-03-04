using System.Text.Json;
using MemNet.MemoryService.Core;
using Retrievo;
using Retrievo.Abstractions;
using Retrievo.Models;

namespace MemNet.MemoryService.Infrastructure;

/// <summary>
/// An <see cref="IEventStore"/> backed by Retrievo for hybrid search and the filesystem for persistence.
/// Events are stored as JSON files (same layout as <see cref="FileEventStore"/>) and indexed
/// in an in-memory Retrievo <see cref="IMutableHybridSearchIndex"/> for BM25 lexical retrieval.
/// The search index is derived state; the filesystem is the source of truth.
/// </summary>
public sealed class RetrievoEventStore : IEventStore, IDisposable
{
    private readonly StorageOptions _options;
    private readonly object _writeLock = new();
    private readonly Dictionary<string, EventDigest> _digestCache = new(StringComparer.Ordinal);
    private IMutableHybridSearchIndex _index;
    private bool _disposed;

    public RetrievoEventStore(StorageOptions options)
    {
        _options = options;
        _index = RebuildIndex();
    }

    /// <inheritdoc />
    public async Task WriteAsync(EventDigest digest, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var filePath = ResolveEventPath(_options.DataRoot, digest.TenantId, digest.UserId, digest.EventId);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        var json = JsonSerializer.Serialize(digest, JsonDefaults.Options);
        await File.WriteAllTextAsync(filePath, json, cancellationToken);

        var compositeKey = CompositeKey(digest.TenantId, digest.UserId, digest.EventId);
        lock (_writeLock)
        {
            _digestCache[compositeKey] = digest;
            _index.Upsert(ToDocument(digest));
            _index.Commit();
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<EventDigest>> QueryAsync(
        string tenantId,
        string userId,
        EventSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // When there is no text query, fall back to a disk scan (matching FileEventStore behavior).
        // Retrievo requires a non-empty text query for lexical search.
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return FallbackDiskScanAsync(tenantId, userId, request, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var query = BuildHybridQuery(tenantId, userId, request);
        List<EventDigest> digests;
        lock (_writeLock)
        {
            var response = _index.Search(query);
            digests = new List<EventDigest>(response.Results.Count);
            foreach (var result in response.Results)
            {
                if (_digestCache.TryGetValue(result.Id, out var digest))
                {
                    digests.Add(digest);
                }
            }
        }

        return Task.FromResult<IReadOnlyList<EventDigest>>(digests);
    }

    public void Dispose()
    {
        lock (_writeLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_index is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    /// <summary>
    /// Scans all tenant/user event directories and builds the initial Retrievo index.
    /// </summary>
    private IMutableHybridSearchIndex RebuildIndex()
    {
        var builder = new MutableHybridSearchIndexBuilder();
        var tenantsRoot = Path.Combine(_options.DataRoot, "tenants");

        if (Directory.Exists(tenantsRoot))
        {
            foreach (var tenantDir in Directory.EnumerateDirectories(tenantsRoot))
            {
                var usersDir = Path.Combine(tenantDir, "users");
                if (!Directory.Exists(usersDir))
                {
                    continue;
                }

                foreach (var userDir in Directory.EnumerateDirectories(usersDir))
                {
                    var eventsDir = Path.Combine(userDir, "events");
                    if (!Directory.Exists(eventsDir))
                    {
                        continue;
                    }

                    foreach (var file in Directory.EnumerateFiles(eventsDir, "*.json", SearchOption.TopDirectoryOnly))
                    {
                        try
                        {
                            var json = File.ReadAllText(file);
                            var digest = JsonSerializer.Deserialize<EventDigest>(json, JsonDefaults.Options);
                            if (digest is null)
                            {
                                continue;
                            }

                            _digestCache[CompositeKey(digest.TenantId, digest.UserId, digest.EventId)] = digest;
                            builder.AddDocument(ToDocument(digest));
                        }
                        catch (JsonException)
                        {
                            // Skip malformed event files — source of truth is the file, but we can't index garbage.
                        }
                    }
                }
            }
        }

        return builder.Build();
    }

    /// <summary>
    /// Fallback for empty-query searches: scan event JSON files from disk and apply in-memory filters.
    /// Matches the existing <see cref="FileEventStore"/> behavior for browse/list scenarios.
    /// </summary>
    private async Task<IReadOnlyList<EventDigest>> FallbackDiskScanAsync(
        string tenantId,
        string userId,
        EventSearchRequest request,
        CancellationToken cancellationToken)
    {
        var dir = ResolveEventsDirectory(_options.DataRoot, tenantId, userId);
        if (!Directory.Exists(dir))
        {
            return Array.Empty<EventDigest>();
        }

        var files = Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly);
        var list = new List<EventDigest>();

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

            list.Add(item);
        }

        var topK = request.TopK <= 0 ? 10 : request.TopK;
        return list
            .OrderByDescending(x => x.Timestamp)
            .Take(topK)
            .ToList();
    }

    private static bool PassesFilters(EventDigest item, EventSearchRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ServiceId)
            && !string.Equals(item.ServiceId, request.ServiceId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.SourceType)
            && !string.Equals(item.SourceType, request.SourceType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.ProjectId)
            && !item.ProjectIds.Contains(request.ProjectId, StringComparer.OrdinalIgnoreCase))
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

    private static HybridQuery BuildHybridQuery(string tenantId, string userId, EventSearchRequest request)
    {
        var metadataFilters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tenant_id"] = tenantId,
            ["user_id"] = userId
        };

        if (!string.IsNullOrWhiteSpace(request.ServiceId))
        {
            metadataFilters["service_id"] = request.ServiceId.ToLowerInvariant();
        }

        if (!string.IsNullOrWhiteSpace(request.SourceType))
        {
            metadataFilters["source_type"] = request.SourceType.ToLowerInvariant();
        }

        Dictionary<string, string>? containsFilters = null;
        if (!string.IsNullOrWhiteSpace(request.ProjectId))
        {
            containsFilters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["project_ids"] = request.ProjectId.ToLowerInvariant()
            };
        }

        List<MetadataRangeFilter>? rangeFilters = null;
        if (request.From.HasValue || request.To.HasValue)
        {
            rangeFilters = new List<MetadataRangeFilter>
            {
                new()
                {
                    Key = "timestamp",
                    Min = request.From?.ToUniversalTime().ToString("O"),
                    Max = request.To?.ToUniversalTime().ToString("O")
                }
            };
        }

        var topK = request.TopK <= 0 ? 10 : request.TopK;

        return new HybridQuery
        {
            Text = request.Query,
            TopK = topK,
            MetadataFilters = metadataFilters,
            MetadataContainsFilters = containsFilters,
            MetadataContainsDelimiter = '|',
            MetadataRangeFilters = rangeFilters
        };
    }

    private static Document ToDocument(EventDigest digest)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tenant_id"] = digest.TenantId,
            ["user_id"] = digest.UserId,
            ["service_id"] = digest.ServiceId.ToLowerInvariant(),
            ["source_type"] = digest.SourceType.ToLowerInvariant(),
            ["timestamp"] = digest.Timestamp.ToUniversalTime().ToString("O"),
            ["project_ids"] = string.Join('|', digest.ProjectIds.Select(p => p.ToLowerInvariant()))
        };

        return new Document
        {
            Id = CompositeKey(digest.TenantId, digest.UserId, digest.EventId),
            Title = string.Join(' ', digest.Keywords),
            Body = digest.Digest,
            Metadata = metadata
        };
    }

    private static string ResolveEventsDirectory(string dataRoot, string tenantId, string userId)
    {
        return Path.Combine(dataRoot, "tenants", tenantId, "users", userId, "events");
    }

    private static string ResolveEventPath(string dataRoot, string tenantId, string userId, string eventId)
    {
        return Path.Combine(ResolveEventsDirectory(dataRoot, tenantId, userId), $"{eventId}.json");
    }

    private static string CompositeKey(string tenantId, string userId, string eventId)
    {
        return $"{tenantId}/{userId}/{eventId}";
    }
}
