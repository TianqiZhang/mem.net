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
        var hasTextEdits = request.Edits is { Count: > 0 };
        var hasPatchOps = request.Ops.Count > 0;
        Guard.True(hasTextEdits || hasPatchOps, "INVALID_PATCH", "Patch request requires either edits[] or ops[].", StatusCodes.Status400BadRequest);
        Guard.True(
            (hasTextEdits ? request.Edits!.Count : request.Ops.Count) <= MaxOpsPerPatch,
            "PATCH_TOO_LARGE",
            "Patch operation count exceeds limit.",
            StatusCodes.Status422UnprocessableEntity);

        var existing = await documentStore.GetAsync(key, cancellationToken);
        if (existing is null)
        {
            throw new ApiException(StatusCodes.Status404NotFound, "DOCUMENT_NOT_FOUND", "Cannot patch missing document.");
        }

        var now = DateTimeOffset.UtcNow;
        var (patchedEnvelope, auditOps) = hasTextEdits
            ? ApplyTextEdits(existing.Envelope, request.Edits!, now, actor)
            : ApplyJsonPatch(existing.Envelope, request.Ops, now, actor);

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
            Ops: auditOps,
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

        Guard.True(request.Files.Count > 0, "MISSING_ASSEMBLY_TARGETS", "Provide at least one file ref for context assembly.", StatusCodes.Status400BadRequest);

        var maxDocs = request.MaxDocs.GetValueOrDefault(4);
        var maxCharsTotal = request.MaxCharsTotal.GetValueOrDefault(30000);

        var result = new List<AssembledFile>();
        var droppedFiles = new List<DroppedFile>();
        var charBudgetUsed = 0;

        foreach (var requested in request.Files)
        {
            if (result.Count >= maxDocs)
            {
                droppedFiles.Add(new DroppedFile(requested.Path, "max_docs"));
                continue;
            }

            var key = new DocumentKey(tenantId, userId, requested.Path);
            var doc = await documentStore.GetAsync(key, cancellationToken);
            if (doc is null)
            {
                continue;
            }

            var chars = JsonSerializer.Serialize(doc.Envelope, JsonDefaults.Options).Length;
            if (charBudgetUsed + chars > maxCharsTotal)
            {
                droppedFiles.Add(new DroppedFile(requested.Path, "max_chars_total"));
                continue;
            }

            charBudgetUsed += chars;
            result.Add(new AssembledFile(requested.Path, doc.ETag, doc.Envelope));
        }

        return new AssembleContextResponse(
            Files: result,
            DroppedFiles: droppedFiles);
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

    private sealed class ScopeHandle : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private static (DocumentEnvelope Envelope, IReadOnlyList<PatchOperation> AuditOps) ApplyJsonPatch(
        DocumentEnvelope existing,
        IReadOnlyList<PatchOperation> ops,
        DateTimeOffset now,
        string actor)
    {
        var rawNode = JsonSerializer.SerializeToNode(existing, JsonDefaults.Options) as JsonObject
            ?? throw new ApiException(StatusCodes.Status500InternalServerError, "SERIALIZATION_ERROR", "Failed to serialize existing envelope.");

        var patchedNode = JsonPatchEngine.Apply(rawNode, ops);
        var patchedEnvelope = patchedNode.Deserialize<DocumentEnvelope>(JsonDefaults.Options)
            ?? throw new ApiException(StatusCodes.Status422UnprocessableEntity, "INVALID_DOCUMENT", "Patch produced invalid document structure.");

        patchedEnvelope = patchedEnvelope with
        {
            UpdatedAt = now,
            UpdatedBy = actor
        };

        return (patchedEnvelope, ops);
    }

    private static (DocumentEnvelope Envelope, IReadOnlyList<PatchOperation> AuditOps) ApplyTextEdits(
        DocumentEnvelope existing,
        IReadOnlyList<TextPatchEdit> edits,
        DateTimeOffset now,
        string actor)
    {
        var content = existing.Content.DeepClone() as JsonObject
            ?? throw new ApiException(StatusCodes.Status422UnprocessableEntity, "INVALID_DOCUMENT", "Document content must be an object.");

        var text = content["text"]?.GetValue<string>();
        if (text is null)
        {
            throw new ApiException(
                StatusCodes.Status422UnprocessableEntity,
                "PATCH_TEXT_NOT_FOUND",
                "Deterministic text patch requires /content/text string.");
        }

        foreach (var edit in edits)
        {
            Guard.True(!string.IsNullOrEmpty(edit.OldText), "INVALID_TEXT_PATCH", "old_text must not be empty.", StatusCodes.Status400BadRequest);
            text = ApplySingleTextEdit(text, edit);
        }

        content["text"] = text;
        var patchedEnvelope = existing with
        {
            Content = content,
            UpdatedAt = now,
            UpdatedBy = actor
        };

        // Keep audit contract stable; detailed edit payload can be reconstructed from evidence and before/after snapshots.
        return (patchedEnvelope, Array.Empty<PatchOperation>());
    }

    private static string ApplySingleTextEdit(string content, TextPatchEdit edit)
    {
        var matches = FindMatchIndexes(content, edit.OldText);
        if (matches.Count == 0)
        {
            throw new ApiException(
                StatusCodes.Status422UnprocessableEntity,
                "PATCH_MATCH_NOT_FOUND",
                "No matching old_text was found in file content.");
        }

        int index;
        if (edit.Occurrence.HasValue)
        {
            if (edit.Occurrence.Value <= 0 || edit.Occurrence.Value > matches.Count)
            {
                throw new ApiException(
                    StatusCodes.Status422UnprocessableEntity,
                    "PATCH_OCCURRENCE_OUT_OF_RANGE",
                    "occurrence is out of range for old_text matches.");
            }

            index = matches[edit.Occurrence.Value - 1];
        }
        else
        {
            if (matches.Count > 1)
            {
                throw new ApiException(
                    StatusCodes.Status422UnprocessableEntity,
                    "PATCH_MATCH_AMBIGUOUS",
                    "old_text matched multiple locations; provide occurrence.");
            }

            index = matches[0];
        }

        return content.Remove(index, edit.OldText.Length).Insert(index, edit.NewText);
    }

    private static List<int> FindMatchIndexes(string content, string oldText)
    {
        var indexes = new List<int>();
        var start = 0;
        while (start <= content.Length - oldText.Length)
        {
            var match = content.IndexOf(oldText, start, StringComparison.Ordinal);
            if (match < 0)
            {
                break;
            }

            indexes.Add(match);
            start = match + oldText.Length;
        }

        return indexes;
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
