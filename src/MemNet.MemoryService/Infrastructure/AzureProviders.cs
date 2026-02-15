using MemNet.MemoryService.Core;
using Microsoft.AspNetCore.Http;

namespace MemNet.MemoryService.Infrastructure;

public sealed class AzureBlobDocumentStore : IDocumentStore
{
    private static ApiException NotConfigured(string capability)
    {
        return new ApiException(
            StatusCodes.Status501NotImplemented,
            "AZURE_PROVIDER_NOT_IMPLEMENTED",
            $"Azure provider scaffolding exists, but '{capability}' is not implemented yet.");
    }

    public Task<DocumentRecord?> GetAsync(DocumentKey key, CancellationToken cancellationToken = default)
    {
        throw NotConfigured("document_get");
    }

    public Task<DocumentRecord> UpsertAsync(DocumentKey key, DocumentEnvelope envelope, string? ifMatch, CancellationToken cancellationToken = default)
    {
        throw NotConfigured("document_upsert");
    }

    public Task<bool> ExistsAsync(DocumentKey key, CancellationToken cancellationToken = default)
    {
        throw NotConfigured("document_exists");
    }
}

public sealed class AzureBlobEventStore : IEventStore
{
    private static ApiException NotConfigured(string capability)
    {
        return new ApiException(
            StatusCodes.Status501NotImplemented,
            "AZURE_PROVIDER_NOT_IMPLEMENTED",
            $"Azure provider scaffolding exists, but '{capability}' is not implemented yet.");
    }

    public Task WriteAsync(EventDigest digest, CancellationToken cancellationToken = default)
    {
        throw NotConfigured("event_write");
    }

    public Task<IReadOnlyList<EventDigest>> QueryAsync(string tenantId, string userId, EventSearchRequest request, CancellationToken cancellationToken = default)
    {
        throw NotConfigured("event_query");
    }
}

public sealed class AzureBlobAuditStore : IAuditStore
{
    private static ApiException NotConfigured(string capability)
    {
        return new ApiException(
            StatusCodes.Status501NotImplemented,
            "AZURE_PROVIDER_NOT_IMPLEMENTED",
            $"Azure provider scaffolding exists, but '{capability}' is not implemented yet.");
    }

    public Task WriteAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        throw NotConfigured("audit_write");
    }
}
