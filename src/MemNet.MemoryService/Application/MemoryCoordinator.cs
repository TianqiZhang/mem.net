using System.Text.Json;
using System.Text.Json.Nodes;
using MemNet.MemoryService.Infrastructure;
using Microsoft.AspNetCore.Http;

namespace MemNet.MemoryService.Core;

public sealed class MemoryCoordinator(
    IDocumentStore documentStore,
    IEventStore eventStore,
    IAuditStore auditStore,
    ILogger<MemoryCoordinator> logger)
{
    private const int MaxOpsPerPatch = 100;
    private const int MaxDocumentChars = 256_000;
    private static readonly IDisposable NoopScope = new ScopeHandle();

    public async Task<DocumentRecord> GetDocumentAsync(DocumentKey key, CancellationToken cancellationToken = default)
    {
        using var scope = BeginScope("document_get", key.TenantId, key.UserId, key.Path);
        logger.LogInformation("Reading document.");

        var result = await documentStore.GetAsync(key, cancellationToken);
        if (result is null)
        {
            throw new ApiException(StatusCodes.Status404NotFound, "DOCUMENT_NOT_FOUND", "Requested document was not found.");
        }

        logger.LogInformation("Document read completed.");
        return result;
    }

    public async Task<MutationResponse> PatchDocumentAsync(
        DocumentKey key,
        PatchDocumentRequest request,
        string ifMatch,
        string actor,
        CancellationToken cancellationToken = default)
    {
        using var scope = BeginScope("document_patch", key.TenantId, key.UserId, key.Path);
        logger.LogInformation("Patching document.");

        Guard.True(!string.IsNullOrWhiteSpace(ifMatch), "MISSING_IF_MATCH", "If-Match header is required.", StatusCodes.Status400BadRequest);
        Guard.True(request.Ops.Count > 0, "INVALID_PATCH", "Patch operations are required.", StatusCodes.Status400BadRequest);
        Guard.True(request.Ops.Count <= MaxOpsPerPatch, "PATCH_TOO_LARGE", "Patch operation count exceeds limit.", StatusCodes.Status422UnprocessableEntity);

        var existing = await documentStore.GetAsync(key, cancellationToken);
        if (existing is null)
        {
            throw new ApiException(StatusCodes.Status404NotFound, "DOCUMENT_NOT_FOUND", "Cannot patch missing document.");
        }

        var rawNode = JsonSerializer.SerializeToNode(existing.Envelope, JsonDefaults.Options) as JsonObject
            ?? throw new ApiException(StatusCodes.Status500InternalServerError, "SERIALIZATION_ERROR", "Failed to serialize existing envelope.");

        var patchedNode = JsonPatchEngine.Apply(rawNode, request.Ops);
        var patchedEnvelope = patchedNode.Deserialize<DocumentEnvelope>(JsonDefaults.Options)
            ?? throw new ApiException(StatusCodes.Status422UnprocessableEntity, "INVALID_DOCUMENT", "Patch produced invalid document structure.");

        var now = DateTimeOffset.UtcNow;
        patchedEnvelope = patchedEnvelope with
        {
            UpdatedAt = now,
            UpdatedBy = actor
        };

        ValidateCoreEnvelope(patchedEnvelope);

        var saved = await documentStore.UpsertAsync(key, patchedEnvelope, ifMatch, cancellationToken);
        var response = new MutationResponse(saved.ETag, saved.Envelope);

        await auditStore.WriteAsync(new AuditRecord(
            ChangeId: $"chg_{Guid.NewGuid():N}",
            Actor: actor,
            TenantId: key.TenantId,
            UserId: key.UserId,
            Path: key.Path,
            PreviousETag: ifMatch,
            NewETag: saved.ETag,
            Reason: request.Reason,
            Ops: request.Ops,
            Timestamp: now,
            EvidenceMessageIds: request.Evidence?.MessageIds), cancellationToken);

        logger.LogInformation("Patch completed.");
        return response;
    }

    public async Task<MutationResponse> ReplaceDocumentAsync(
        DocumentKey key,
        ReplaceDocumentRequest request,
        string ifMatch,
        string actor,
        CancellationToken cancellationToken = default)
    {
        using var scope = BeginScope("document_replace", key.TenantId, key.UserId, key.Path);
        logger.LogInformation("Replacing document.");

        Guard.True(!string.IsNullOrWhiteSpace(ifMatch), "MISSING_IF_MATCH", "If-Match header is required.", StatusCodes.Status400BadRequest);

        var now = DateTimeOffset.UtcNow;
        var replacement = request.Document with { UpdatedAt = now, UpdatedBy = actor };
        ValidateCoreEnvelope(replacement);

        var saved = await documentStore.UpsertAsync(key, replacement, ifMatch, cancellationToken);
        var response = new MutationResponse(saved.ETag, saved.Envelope);

        await auditStore.WriteAsync(new AuditRecord(
            ChangeId: $"chg_{Guid.NewGuid():N}",
            Actor: actor,
            TenantId: key.TenantId,
            UserId: key.UserId,
            Path: key.Path,
            PreviousETag: ifMatch,
            NewETag: saved.ETag,
            Reason: request.Reason,
            Ops: Array.Empty<PatchOperation>(),
            Timestamp: now,
            EvidenceMessageIds: request.Evidence?.MessageIds), cancellationToken);

        logger.LogInformation("Replace completed.");
        return response;
    }

    public async Task<AssembleContextResponse> AssembleContextAsync(
        string tenantId,
        string userId,
        AssembleContextRequest request,
        CancellationToken cancellationToken = default)
    {
        using var scope = BeginScope("context_assemble", tenantId, userId);
        logger.LogInformation("Assembling context.");

        Guard.True(request.Documents.Count > 0, "MISSING_ASSEMBLY_TARGETS", "Provide at least one document ref for context assembly.", StatusCodes.Status400BadRequest);

        var maxDocs = request.MaxDocs.GetValueOrDefault(4);
        var maxCharsTotal = request.MaxCharsTotal.GetValueOrDefault(30000);

        var result = new List<AssembledDocument>();
        var droppedDocuments = new List<DroppedDocument>();
        var charBudgetUsed = 0;

        foreach (var requested in request.Documents)
        {
            if (result.Count >= maxDocs)
            {
                droppedDocuments.Add(new DroppedDocument(requested.Namespace, requested.Path, "max_docs"));
                continue;
            }

            var key = new DocumentKey(tenantId, userId, BuildScopedPath(requested.Namespace, requested.Path));
            var doc = await documentStore.GetAsync(key, cancellationToken);
            if (doc is null)
            {
                continue;
            }

            var chars = JsonSerializer.Serialize(doc.Envelope, JsonDefaults.Options).Length;
            if (charBudgetUsed + chars > maxCharsTotal)
            {
                droppedDocuments.Add(new DroppedDocument(requested.Namespace, requested.Path, "max_chars_total"));
                continue;
            }

            charBudgetUsed += chars;
            result.Add(new AssembledDocument(requested.Namespace, requested.Path, doc.ETag, doc.Envelope));
        }

        return new AssembleContextResponse(
            Documents: result,
            DroppedDocuments: droppedDocuments);
    }

    public Task WriteEventAsync(EventDigest digest, CancellationToken cancellationToken = default)
    {
        using var scope = BeginScope("event_write", digest.TenantId, digest.UserId, eventId: digest.EventId);
        logger.LogInformation("Writing event digest.");

        Guard.True(!string.IsNullOrWhiteSpace(digest.EventId), "INVALID_EVENT", "event_id is required.", StatusCodes.Status400BadRequest);
        Guard.True(!string.IsNullOrWhiteSpace(digest.Digest), "INVALID_EVENT", "digest is required.", StatusCodes.Status400BadRequest);
        return eventStore.WriteAsync(digest, cancellationToken);
    }

    public async Task<SearchEventsResponse> SearchEventsAsync(string tenantId, string userId, SearchEventsRequest request, CancellationToken cancellationToken = default)
    {
        using var scope = BeginScope("event_search", tenantId, userId);
        logger.LogInformation("Searching event digests.");

        var results = await eventStore.QueryAsync(
            tenantId,
            userId,
            new EventSearchRequest(
                Query: request.Query,
                ServiceId: request.ServiceId,
                SourceType: request.SourceType,
                ProjectId: request.ProjectId,
                From: request.From,
                To: request.To,
                TopK: request.TopK.GetValueOrDefault(10)),
            cancellationToken);

        return new SearchEventsResponse(results);
    }

    private IDisposable BeginScope(
        string operation,
        string tenantId,
        string userId,
        string? path = null,
        string? eventId = null)
    {
        return logger.BeginScope(new Dictionary<string, object?>
        {
            ["operation"] = operation,
            ["tenant_id"] = tenantId,
            ["user_id"] = userId,
            ["path"] = path,
            ["event_id"] = eventId
        }) ?? NoopScope;
    }

    private static string BuildScopedPath(string @namespace, string path)
    {
        var ns = (@namespace ?? string.Empty).Trim('/');
        var leaf = (path ?? string.Empty).Trim('/');
        return string.IsNullOrWhiteSpace(ns) ? leaf : $"{ns}/{leaf}";
    }

    private sealed class ScopeHandle : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private static void ValidateCoreEnvelope(DocumentEnvelope envelope)
    {
        Guard.True(!string.IsNullOrWhiteSpace(envelope.DocId), "INVALID_DOCUMENT", "Envelope doc_id is required.", StatusCodes.Status422UnprocessableEntity);
        Guard.True(!string.IsNullOrWhiteSpace(envelope.SchemaId), "INVALID_DOCUMENT", "Envelope schema_id is required.", StatusCodes.Status422UnprocessableEntity);
        Guard.True(!string.IsNullOrWhiteSpace(envelope.SchemaVersion), "INVALID_DOCUMENT", "Envelope schema_version is required.", StatusCodes.Status422UnprocessableEntity);

        var serialized = JsonSerializer.Serialize(envelope, JsonDefaults.Options);
        Guard.True(
            serialized.Length <= MaxDocumentChars,
            "DOCUMENT_SIZE_EXCEEDED",
            $"Document exceeds service max document size ({MaxDocumentChars} chars).",
            StatusCodes.Status422UnprocessableEntity);
    }
}
