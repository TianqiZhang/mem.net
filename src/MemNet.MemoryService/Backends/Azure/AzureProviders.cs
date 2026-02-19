#if MEMNET_ENABLE_AZURE_SDK
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Search.Documents.Models;
using Azure.Storage.Blobs.Models;
using MemNet.MemoryService.Core;
using Microsoft.AspNetCore.Http;

namespace MemNet.MemoryService.Infrastructure;

public sealed class AzureBlobDocumentStore(AzureClients clients) : IDocumentStore
{
    public async Task<DocumentRecord?> GetAsync(DocumentKey key, CancellationToken cancellationToken = default)
    {
        var blobClient = clients.DocumentsContainer.GetBlobClient(AzurePathBuilder.DocumentPath(key));

        try
        {
            var download = await blobClient.DownloadContentAsync(cancellationToken: cancellationToken);
            var envelope = JsonSerializer.Deserialize<DocumentEnvelope>(download.Value.Content, JsonDefaults.Options)
                ?? throw new ApiException(StatusCodes.Status500InternalServerError, "STORE_DESERIALIZATION_FAILED", "Failed to parse document from storage.");

            return new DocumentRecord(envelope, download.Value.Details.ETag.ToString());
        }
        catch (RequestFailedException ex) when (ex.Status == StatusCodes.Status404NotFound)
        {
            return null;
        }
        catch (RequestFailedException ex)
        {
            throw AzureErrorMapper.ToApiException(ex, "AZURE_DOCUMENT_GET_FAILED", "Failed to read document from Azure Blob Storage.");
        }
    }

    public async Task<IReadOnlyList<FileListItem>> ListAsync(
        string tenantId,
        string userId,
        string? prefix,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var documentsPrefix = AzurePathBuilder.DocumentsPrefix(tenantId, userId);
        var normalizedPrefix = NormalizePrefix(prefix);
        var blobPrefix = string.IsNullOrWhiteSpace(normalizedPrefix)
            ? documentsPrefix
            : $"{documentsPrefix}{normalizedPrefix}";

        var matches = new List<FileListItem>();
        try
        {
            await foreach (var blob in clients.DocumentsContainer.GetBlobsAsync(prefix: blobPrefix, cancellationToken: cancellationToken))
            {
                var relativePath = blob.Name.StartsWith(documentsPrefix, StringComparison.Ordinal)
                    ? blob.Name[documentsPrefix.Length..]
                    : blob.Name;

                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    continue;
                }

                var lastModifiedUtc = blob.Properties.LastModified?.UtcDateTime ?? DateTime.UnixEpoch;
                matches.Add(new FileListItem(relativePath, new DateTimeOffset(lastModifiedUtc, TimeSpan.Zero)));
            }
        }
        catch (RequestFailedException ex) when (ex.Status == StatusCodes.Status404NotFound)
        {
            return Array.Empty<FileListItem>();
        }
        catch (RequestFailedException ex)
        {
            throw AzureErrorMapper.ToApiException(ex, "AZURE_DOCUMENT_LIST_FAILED", "Failed to list documents from Azure Blob Storage.");
        }

        return matches
            .OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToArray();
    }

    public async Task<DocumentRecord> UpsertAsync(
        DocumentKey key,
        DocumentEnvelope envelope,
        string? ifMatch,
        CancellationToken cancellationToken = default)
    {
        await clients.DocumentsContainer.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        var blobClient = clients.DocumentsContainer.GetBlobClient(AzurePathBuilder.DocumentPath(key));
        var serialized = JsonSerializer.Serialize(envelope, JsonDefaults.Options);

        var uploadOptions = new BlobUploadOptions();
        if (!string.IsNullOrWhiteSpace(ifMatch))
        {
            uploadOptions.Conditions = ifMatch == "*"
                ? new BlobRequestConditions { IfNoneMatch = ETag.All }
                : new BlobRequestConditions { IfMatch = new ETag(ifMatch) };
        }

        try
        {
            var response = await blobClient.UploadAsync(BinaryData.FromString(serialized), uploadOptions, cancellationToken);
            return new DocumentRecord(envelope, response.Value.ETag.ToString());
        }
        catch (RequestFailedException ex) when (ex.Status == StatusCodes.Status412PreconditionFailed)
        {
            string? latestEtag = null;
            try
            {
                var props = await blobClient.GetPropertiesAsync(cancellationToken: cancellationToken);
                latestEtag = props.Value.ETag.ToString();
            }
            catch (RequestFailedException propEx) when (propEx.Status == StatusCodes.Status404NotFound)
            {
                latestEtag = null;
            }

            var details = latestEtag is null
                ? null
                : new Dictionary<string, string> { ["latest_etag"] = latestEtag };

            throw new ApiException(
                StatusCodes.Status412PreconditionFailed,
                "ETAG_MISMATCH",
                "If-Match does not match latest document version.",
                details);
        }
        catch (RequestFailedException ex)
        {
            throw AzureErrorMapper.ToApiException(ex, "AZURE_DOCUMENT_UPSERT_FAILED", "Failed to write document to Azure Blob Storage.");
        }
    }

    public async Task<bool> ExistsAsync(DocumentKey key, CancellationToken cancellationToken = default)
    {
        var blobClient = clients.DocumentsContainer.GetBlobClient(AzurePathBuilder.DocumentPath(key));

        try
        {
            var response = await blobClient.ExistsAsync(cancellationToken);
            return response.Value;
        }
        catch (RequestFailedException ex)
        {
            throw AzureErrorMapper.ToApiException(ex, "AZURE_DOCUMENT_EXISTS_FAILED", "Failed to check document existence in Azure Blob Storage.");
        }
    }

    private static string? NormalizePrefix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Replace('\\', '/').Trim().TrimStart('/');
        if (normalized.Contains("..", StringComparison.Ordinal))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "INVALID_PATH_PREFIX", "Prefix must not contain '..'.");
        }

        return normalized;
    }
}

public sealed class AzureBlobEventStore(AzureClients clients) : IEventStore
{
    public async Task WriteAsync(EventDigest digest, CancellationToken cancellationToken = default)
    {
        await clients.EventsContainer.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        var blobClient = clients.EventsContainer.GetBlobClient(AzurePathBuilder.EventPath(digest.TenantId, digest.UserId, digest.EventId));
        var payload = JsonSerializer.Serialize(digest, JsonDefaults.Options);

        try
        {
            await blobClient.UploadAsync(BinaryData.FromString(payload), overwrite: true, cancellationToken: cancellationToken);
        }
        catch (RequestFailedException ex)
        {
            throw AzureErrorMapper.ToApiException(ex, "AZURE_EVENT_WRITE_FAILED", "Failed to write event digest blob.");
        }

        if (clients.Search is null)
        {
            return;
        }

        try
        {
            var doc = ToSearchDocument(digest);
            await clients.Search.UploadDocumentsAsync(
                [doc],
                new Azure.Search.Documents.IndexDocumentsOptions { ThrowOnAnyError = true },
                cancellationToken);
        }
        catch (RequestFailedException)
        {
            // Search is a derived index. Blob persistence remains source of truth.
        }
    }

    public async Task<IReadOnlyList<EventDigest>> QueryAsync(
        string tenantId,
        string userId,
        EventSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        if (clients.Search is not null)
        {
            try
            {
                var fromSearch = await QueryFromSearchAsync(tenantId, userId, request, cancellationToken);
                if (fromSearch.Count > 0)
                {
                    return fromSearch;
                }
            }
            catch (RequestFailedException)
            {
                // Fall back to blob scan if search is unavailable or misconfigured.
            }
        }

        return await QueryFromBlobsAsync(tenantId, userId, request, cancellationToken);
    }

    private async Task<IReadOnlyList<EventDigest>> QueryFromSearchAsync(
        string tenantId,
        string userId,
        EventSearchRequest request,
        CancellationToken cancellationToken)
    {
        var topK = request.TopK <= 0 ? 10 : request.TopK;
        var searchText = string.IsNullOrWhiteSpace(request.Query) ? "*" : request.Query;

        var options = new Azure.Search.Documents.SearchOptions
        {
            Size = topK,
            Filter = BuildFilter(tenantId, userId, request)
        };

        if (string.IsNullOrWhiteSpace(request.Query))
        {
            options.OrderBy.Add("timestamp desc");
        }

        var response = await clients.Search!.SearchAsync<SearchDocument>(searchText, options, cancellationToken);
        var results = new List<EventDigest>();

        await foreach (var item in response.Value.GetResultsAsync().WithCancellation(cancellationToken))
        {
            // Always hydrate from blob source-of-truth so evidence remains opaque and lossless.
            var eventId = ReadString(item.Document, "event_id") ?? ReadString(item.Document, "id");
            EventDigest? mapped = null;
            if (!string.IsNullOrWhiteSpace(eventId))
            {
                mapped = await ReadEventDigestFromBlobAsync(tenantId, userId, eventId, cancellationToken);
            }

            mapped ??= FromSearchDocument(item.Document);
            if (mapped is not null)
            {
                if (!PassesFilters(mapped, request))
                {
                    continue;
                }

                results.Add(mapped);
            }

            if (results.Count >= topK)
            {
                break;
            }
        }

        return results;
    }

    private async Task<IReadOnlyList<EventDigest>> QueryFromBlobsAsync(
        string tenantId,
        string userId,
        EventSearchRequest request,
        CancellationToken cancellationToken)
    {
        var prefix = AzurePathBuilder.EventsPrefix(tenantId, userId);
        var queryTokens = Tokenize(request.Query);
        var matches = new List<(EventDigest Event, double Score)>();

        try
        {
            await foreach (var blob in clients.EventsContainer.GetBlobsAsync(prefix: prefix, cancellationToken: cancellationToken))
            {
                var eventId = Path.GetFileNameWithoutExtension(blob.Name);
                var digest = await ReadEventDigestFromBlobAsync(tenantId, userId, eventId, cancellationToken);

                if (digest is null || !PassesFilters(digest, request))
                {
                    continue;
                }

                var score = Score(digest, queryTokens);
                matches.Add((digest, score));
            }
        }
        catch (RequestFailedException ex) when (ex.Status == StatusCodes.Status404NotFound)
        {
            return Array.Empty<EventDigest>();
        }
        catch (RequestFailedException ex)
        {
            throw AzureErrorMapper.ToApiException(ex, "AZURE_EVENT_QUERY_FAILED", "Failed to query event digests from Azure Blob Storage.");
        }

        var topK = request.TopK <= 0 ? 10 : request.TopK;
        return matches
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Event.Timestamp)
            .Take(topK)
            .Select(x => x.Event)
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

    private static SearchDocument ToSearchDocument(EventDigest digest)
    {
        return new SearchDocument
        {
            ["id"] = BuildSearchDocumentId(digest.TenantId, digest.UserId, digest.EventId),
            ["event_id"] = digest.EventId,
            ["tenant_id"] = digest.TenantId,
            ["user_id"] = digest.UserId,
            ["service_id"] = digest.ServiceId,
            ["timestamp"] = digest.Timestamp,
            ["source_type"] = digest.SourceType,
            ["digest"] = digest.Digest,
            ["keywords"] = digest.Keywords.ToArray(),
            ["project_ids"] = digest.ProjectIds.ToArray()
        };
    }

    private static string BuildSearchDocumentId(string tenantId, string userId, string eventId)
    {
        var raw = $"{tenantId}|{userId}|{eventId}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static EventDigest? FromSearchDocument(SearchDocument doc)
    {
        var eventId = ReadString(doc, "event_id") ?? ReadString(doc, "id");
        var tenantId = ReadString(doc, "tenant_id");
        var userId = ReadString(doc, "user_id");
        var serviceId = ReadString(doc, "service_id");
        var sourceType = ReadString(doc, "source_type");
        var digest = ReadString(doc, "digest");
        var timestamp = ReadDateTimeOffset(doc, "timestamp");

        if (string.IsNullOrWhiteSpace(eventId)
            || string.IsNullOrWhiteSpace(tenantId)
            || string.IsNullOrWhiteSpace(userId)
            || string.IsNullOrWhiteSpace(serviceId)
            || string.IsNullOrWhiteSpace(sourceType)
            || string.IsNullOrWhiteSpace(digest)
            || !timestamp.HasValue)
        {
            return null;
        }

        return new EventDigest(
            EventId: eventId,
            TenantId: tenantId,
            UserId: userId,
            ServiceId: serviceId,
            Timestamp: timestamp.Value,
            SourceType: sourceType,
            Digest: digest,
            Keywords: ReadStringList(doc, "keywords"),
            ProjectIds: ReadStringList(doc, "project_ids"),
            Evidence: null);
    }

    private async Task<EventDigest?> ReadEventDigestFromBlobAsync(
        string tenantId,
        string userId,
        string eventId,
        CancellationToken cancellationToken)
    {
        try
        {
            var blobPath = AzurePathBuilder.EventPath(tenantId, userId, eventId);
            var download = await clients.EventsContainer
                .GetBlobClient(blobPath)
                .DownloadContentAsync(cancellationToken: cancellationToken);

            return JsonSerializer.Deserialize<EventDigest>(download.Value.Content, JsonDefaults.Options);
        }
        catch (RequestFailedException ex) when (ex.Status == StatusCodes.Status404NotFound)
        {
            return null;
        }
        catch (RequestFailedException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string? BuildFilter(string tenantId, string userId, EventSearchRequest request)
    {
        var filters = new List<string>
        {
            $"tenant_id eq '{EscapeFilterValue(tenantId)}'",
            $"user_id eq '{EscapeFilterValue(userId)}'"
        };

        if (!string.IsNullOrWhiteSpace(request.ServiceId))
        {
            filters.Add($"service_id eq '{EscapeFilterValue(request.ServiceId)}'");
        }

        if (!string.IsNullOrWhiteSpace(request.SourceType))
        {
            filters.Add($"source_type eq '{EscapeFilterValue(request.SourceType)}'");
        }

        if (!string.IsNullOrWhiteSpace(request.ProjectId))
        {
            filters.Add($"project_ids/any(p: p eq '{EscapeFilterValue(request.ProjectId)}')");
        }

        if (request.From.HasValue)
        {
            filters.Add($"timestamp ge {request.From.Value.UtcDateTime:O}");
        }

        if (request.To.HasValue)
        {
            filters.Add($"timestamp le {request.To.Value.UtcDateTime:O}");
        }

        return filters.Count == 0 ? null : string.Join(" and ", filters);
    }

    private static string EscapeFilterValue(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static string? ReadString(SearchDocument doc, string key)
    {
        if (!doc.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string text => text,
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
            _ => value.ToString()
        };
    }

    private static DateTimeOffset? ReadDateTimeOffset(SearchDocument doc, string key)
    {
        if (!doc.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(dt),
            JsonElement element when element.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(element.GetString(), out var dto) => dto,
            string text when DateTimeOffset.TryParse(text, out var dto) => dto,
            _ => null
        };
    }

    private static IReadOnlyList<string> ReadStringList(SearchDocument doc, string key)
    {
        if (!doc.TryGetValue(key, out var value) || value is null)
        {
            return Array.Empty<string>();
        }

        if (value is IEnumerable<string> texts)
        {
            return texts.ToArray();
        }

        if (value is JsonElement element && element.ValueKind == JsonValueKind.Array)
        {
            var values = new List<string>();
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } text)
                {
                    values.Add(text);
                }
            }

            return values;
        }

        return Array.Empty<string>();
    }
}

public sealed class AzureBlobAuditStore(AzureClients clients) : IAuditStore
{
    public async Task WriteAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        await clients.AuditContainer.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        var blobClient = clients.AuditContainer.GetBlobClient(AzurePathBuilder.AuditPath(record.TenantId, record.UserId, record.ChangeId));
        var payload = JsonSerializer.Serialize(record, JsonDefaults.Options);

        try
        {
            await blobClient.UploadAsync(BinaryData.FromString(payload), overwrite: true, cancellationToken: cancellationToken);
        }
        catch (RequestFailedException ex)
        {
            throw AzureErrorMapper.ToApiException(ex, "AZURE_AUDIT_WRITE_FAILED", "Failed to write audit record blob.");
        }
    }
}

public sealed class AzureBlobUserDataMaintenanceStore(AzureClients clients) : IUserDataMaintenanceStore
{
    public async Task<ForgetUserResult> ForgetUserAsync(string tenantId, string userId, CancellationToken cancellationToken = default)
    {
        var documentsDeleted = await DeleteBlobsByPrefixAsync(
            clients.DocumentsContainer,
            AzurePathBuilder.DocumentsPrefix(tenantId, userId),
            cancellationToken);

        var eventsDeleted = await DeleteBlobsByPrefixAsync(
            clients.EventsContainer,
            AzurePathBuilder.EventsPrefix(tenantId, userId),
            cancellationToken);

        var auditDeleted = await DeleteBlobsByPrefixAsync(
            clients.AuditContainer,
            AzurePathBuilder.AuditPrefix(tenantId, userId),
            cancellationToken);

        var searchDocumentsDeleted = await DeleteSearchDocumentsByFilterAsync(
            BuildSearchScopeFilter(tenantId, userId),
            cancellationToken);

        return new ForgetUserResult(
            DocumentsDeleted: documentsDeleted,
            EventsDeleted: eventsDeleted,
            AuditDeleted: auditDeleted,
            SnapshotsDeleted: 0,
            SearchDocumentsDeleted: searchDocumentsDeleted);
    }

    public async Task<RetentionSweepResult> ApplyRetentionAsync(
        string tenantId,
        string userId,
        RetentionRules rules,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default)
    {
        var cutoffEvents = asOfUtc.AddDays(-rules.EventsDays);
        var cutoffAudit = asOfUtc.AddDays(-rules.AuditDays);
        var cutoffSnapshots = asOfUtc.AddDays(-rules.SnapshotsDays);

        var eventsDeleted = await DeleteEventsOlderThanAsync(tenantId, userId, cutoffEvents, cancellationToken);
        var auditDeleted = await DeleteAuditOlderThanAsync(tenantId, userId, cutoffAudit, cancellationToken);

        var searchDocumentsDeleted = await DeleteSearchDocumentsByFilterAsync(
            $"{BuildSearchScopeFilter(tenantId, userId)} and timestamp lt {cutoffEvents.UtcDateTime:O}",
            cancellationToken);

        return new RetentionSweepResult(
            EventsDeleted: eventsDeleted,
            AuditDeleted: auditDeleted,
            SnapshotsDeleted: 0,
            SearchDocumentsDeleted: searchDocumentsDeleted,
            CutoffEventsUtc: cutoffEvents,
            CutoffAuditUtc: cutoffAudit,
            CutoffSnapshotsUtc: cutoffSnapshots);
    }

    private async Task<int> DeleteEventsOlderThanAsync(
        string tenantId,
        string userId,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        var prefix = AzurePathBuilder.EventsPrefix(tenantId, userId);
        var deleted = 0;
        try
        {
            await foreach (var blob in clients.EventsContainer.GetBlobsAsync(prefix: prefix, cancellationToken: cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                EventDigest? digest;
                try
                {
                    var download = await clients.EventsContainer.GetBlobClient(blob.Name).DownloadContentAsync(cancellationToken: cancellationToken);
                    digest = JsonSerializer.Deserialize<EventDigest>(download.Value.Content, JsonDefaults.Options);
                }
                catch (RequestFailedException)
                {
                    continue;
                }

                if (digest is null || digest.Timestamp >= cutoff)
                {
                    continue;
                }

                var response = await clients.EventsContainer.DeleteBlobIfExistsAsync(
                    blob.Name,
                    DeleteSnapshotsOption.IncludeSnapshots,
                    cancellationToken: cancellationToken);
                if (response.Value)
                {
                    deleted++;
                }
            }
        }
        catch (RequestFailedException ex) when (ex.Status == StatusCodes.Status404NotFound)
        {
            return 0;
        }
        catch (RequestFailedException ex)
        {
            throw AzureErrorMapper.ToApiException(ex, "AZURE_RETENTION_EVENTS_FAILED", "Failed to apply event retention in Azure Blob Storage.");
        }

        return deleted;
    }

    private async Task<int> DeleteAuditOlderThanAsync(
        string tenantId,
        string userId,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        var prefix = AzurePathBuilder.AuditPrefix(tenantId, userId);
        var deleted = 0;
        try
        {
            await foreach (var blob in clients.AuditContainer.GetBlobsAsync(prefix: prefix, cancellationToken: cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AuditRecord? record;
                try
                {
                    var download = await clients.AuditContainer.GetBlobClient(blob.Name).DownloadContentAsync(cancellationToken: cancellationToken);
                    record = JsonSerializer.Deserialize<AuditRecord>(download.Value.Content, JsonDefaults.Options);
                }
                catch (RequestFailedException)
                {
                    continue;
                }

                if (record is null || record.Timestamp >= cutoff)
                {
                    continue;
                }

                var response = await clients.AuditContainer.DeleteBlobIfExistsAsync(
                    blob.Name,
                    DeleteSnapshotsOption.IncludeSnapshots,
                    cancellationToken: cancellationToken);
                if (response.Value)
                {
                    deleted++;
                }
            }
        }
        catch (RequestFailedException ex) when (ex.Status == StatusCodes.Status404NotFound)
        {
            return 0;
        }
        catch (RequestFailedException ex)
        {
            throw AzureErrorMapper.ToApiException(ex, "AZURE_RETENTION_AUDIT_FAILED", "Failed to apply audit retention in Azure Blob Storage.");
        }

        return deleted;
    }

    private async Task<int> DeleteBlobsByPrefixAsync(
        Azure.Storage.Blobs.BlobContainerClient container,
        string prefix,
        CancellationToken cancellationToken)
    {
        var deleted = 0;
        try
        {
            await foreach (var blob in container.GetBlobsAsync(prefix: prefix, cancellationToken: cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var response = await container.DeleteBlobIfExistsAsync(
                    blob.Name,
                    DeleteSnapshotsOption.IncludeSnapshots,
                    cancellationToken: cancellationToken);
                if (response.Value)
                {
                    deleted++;
                }
            }
        }
        catch (RequestFailedException ex) when (ex.Status == StatusCodes.Status404NotFound)
        {
            return 0;
        }
        catch (RequestFailedException ex)
        {
            throw AzureErrorMapper.ToApiException(ex, "AZURE_FORGET_USER_FAILED", "Failed to delete user data from Azure Blob Storage.");
        }

        return deleted;
    }

    private async Task<int> DeleteSearchDocumentsByFilterAsync(string filter, CancellationToken cancellationToken)
    {
        if (clients.Search is null)
        {
            return 0;
        }

        var ids = new List<string>();
        var deleted = 0;
        try
        {
            var options = new Azure.Search.Documents.SearchOptions
            {
                Size = 1000,
                Filter = filter
            };
            options.Select.Add("id");

            var response = await clients.Search.SearchAsync<SearchDocument>("*", options, cancellationToken);
            await foreach (var item in response.Value.GetResultsAsync().WithCancellation(cancellationToken))
            {
                if (ReadString(item.Document, "id") is { } id && !string.IsNullOrWhiteSpace(id))
                {
                    ids.Add(id);
                }

                if (ids.Count >= 500)
                {
                    deleted += await DeleteSearchBatchAsync(ids, cancellationToken);
                }
            }

            if (ids.Count > 0)
            {
                deleted += await DeleteSearchBatchAsync(ids, cancellationToken);
            }
        }
        catch (RequestFailedException ex)
        {
            throw AzureErrorMapper.ToApiException(ex, "AZURE_SEARCH_DELETE_FAILED", "Failed to delete Azure AI Search documents.");
        }

        return deleted;
    }

    private async Task<int> DeleteSearchBatchAsync(List<string> ids, CancellationToken cancellationToken)
    {
        var batch = IndexDocumentsBatch.Delete("id", ids);
        var response = await clients.Search!.IndexDocumentsAsync(batch, cancellationToken: cancellationToken);
        var deleted = response.Value.Results.Count(x => x.Succeeded);
        ids.Clear();
        return deleted;
    }

    private static string BuildSearchScopeFilter(string tenantId, string userId)
    {
        return $"tenant_id eq '{EscapeFilterValue(tenantId)}' and user_id eq '{EscapeFilterValue(userId)}'";
    }

    private static string EscapeFilterValue(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static string? ReadString(SearchDocument doc, string key)
    {
        if (!doc.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value.ToString();
    }
}

internal static class AzurePathBuilder
{
    public static string DocumentPath(DocumentKey key)
    {
        var safeTenant = SanitizeSegment(key.TenantId);
        var safeUser = SanitizeSegment(key.UserId);
        var safePath = SanitizePath(key.Path);

        return $"tenants/{safeTenant}/users/{safeUser}/files/{safePath}";
    }

    public static string EventPath(string tenantId, string userId, string eventId)
    {
        return $"{EventsPrefix(tenantId, userId)}{SanitizeSegment(eventId)}.json";
    }

    public static string EventsPrefix(string tenantId, string userId)
    {
        return $"tenants/{SanitizeSegment(tenantId)}/users/{SanitizeSegment(userId)}/events/";
    }

    public static string DocumentsPrefix(string tenantId, string userId)
    {
        return $"tenants/{SanitizeSegment(tenantId)}/users/{SanitizeSegment(userId)}/files/";
    }

    public static string AuditPrefix(string tenantId, string userId)
    {
        return $"tenants/{SanitizeSegment(tenantId)}/users/{SanitizeSegment(userId)}/audit/";
    }

    public static string AuditPath(string tenantId, string userId, string changeId)
    {
        return $"{AuditPrefix(tenantId, userId)}{SanitizeSegment(changeId)}.json";
    }

    private static string SanitizeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('/') || value.Contains('\\'))
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
}
#else
using MemNet.MemoryService.Core;
using Microsoft.AspNetCore.Http;

namespace MemNet.MemoryService.Infrastructure;

public sealed class AzureBlobDocumentStore : IDocumentStore
{
    private static ApiException NotEnabled()
    {
        return new ApiException(
            StatusCodes.Status501NotImplemented,
            "AZURE_PROVIDER_NOT_ENABLED",
            "Azure provider build flag is disabled. Rebuild with /p:MemNetEnableAzureSdk=true to enable Azure SDK providers.");
    }

    public Task<DocumentRecord?> GetAsync(DocumentKey key, CancellationToken cancellationToken = default)
    {
        _ = key;
        _ = cancellationToken;
        throw NotEnabled();
    }

    public Task<DocumentRecord> UpsertAsync(DocumentKey key, DocumentEnvelope envelope, string? ifMatch, CancellationToken cancellationToken = default)
    {
        _ = key;
        _ = envelope;
        _ = ifMatch;
        _ = cancellationToken;
        throw NotEnabled();
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
        _ = prefix;
        _ = limit;
        _ = cancellationToken;
        throw NotEnabled();
    }

    public Task<bool> ExistsAsync(DocumentKey key, CancellationToken cancellationToken = default)
    {
        _ = key;
        _ = cancellationToken;
        throw NotEnabled();
    }
}

public sealed class AzureBlobEventStore : IEventStore
{
    private static ApiException NotEnabled()
    {
        return new ApiException(
            StatusCodes.Status501NotImplemented,
            "AZURE_PROVIDER_NOT_ENABLED",
            "Azure provider build flag is disabled. Rebuild with /p:MemNetEnableAzureSdk=true to enable Azure SDK providers.");
    }

    public Task WriteAsync(EventDigest digest, CancellationToken cancellationToken = default)
    {
        _ = digest;
        _ = cancellationToken;
        throw NotEnabled();
    }

    public Task<IReadOnlyList<EventDigest>> QueryAsync(string tenantId, string userId, EventSearchRequest request, CancellationToken cancellationToken = default)
    {
        _ = tenantId;
        _ = userId;
        _ = request;
        _ = cancellationToken;
        throw NotEnabled();
    }
}

public sealed class AzureBlobAuditStore : IAuditStore
{
    private static ApiException NotEnabled()
    {
        return new ApiException(
            StatusCodes.Status501NotImplemented,
            "AZURE_PROVIDER_NOT_ENABLED",
            "Azure provider build flag is disabled. Rebuild with /p:MemNetEnableAzureSdk=true to enable Azure SDK providers.");
    }

    public Task WriteAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        _ = record;
        _ = cancellationToken;
        throw NotEnabled();
    }
}

public sealed class AzureBlobUserDataMaintenanceStore : IUserDataMaintenanceStore
{
    private static ApiException NotEnabled()
    {
        return new ApiException(
            StatusCodes.Status501NotImplemented,
            "AZURE_PROVIDER_NOT_ENABLED",
            "Azure provider build flag is disabled. Rebuild with /p:MemNetEnableAzureSdk=true to enable Azure SDK providers.");
    }

    public Task<ForgetUserResult> ForgetUserAsync(string tenantId, string userId, CancellationToken cancellationToken = default)
    {
        _ = tenantId;
        _ = userId;
        _ = cancellationToken;
        throw NotEnabled();
    }

    public Task<RetentionSweepResult> ApplyRetentionAsync(
        string tenantId,
        string userId,
        RetentionRules rules,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default)
    {
        _ = tenantId;
        _ = userId;
        _ = rules;
        _ = asOfUtc;
        _ = cancellationToken;
        throw NotEnabled();
    }
}
#endif
