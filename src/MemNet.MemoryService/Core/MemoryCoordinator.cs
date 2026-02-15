using System.Text.Json;
using System.Text.Json.Nodes;
using MemNet.MemoryService.Infrastructure;
using Microsoft.AspNetCore.Http;

namespace MemNet.MemoryService.Core;

public sealed class MemoryCoordinator(
    IDocumentStore documentStore,
    IEventStore eventStore,
    IAuditStore auditStore,
    PolicyRegistry policy,
    ILogger<MemoryCoordinator> logger)
{
    private const int MaxOpsPerPatch = 100;
    private static readonly IDisposable NoopScope = new ScopeHandle();

    public async Task<DocumentRecord> GetDocumentAsync(DocumentKey key, CancellationToken cancellationToken = default)
    {
        using var scope = BeginScope("document_get", key.TenantId, key.UserId, key.Namespace, key.Path);
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
        using var scope = BeginScope("document_patch", key.TenantId, key.UserId, key.Namespace, key.Path);
        logger.LogInformation("Patching document.");

        Guard.True(!string.IsNullOrWhiteSpace(ifMatch), "MISSING_IF_MATCH", "If-Match header is required.", StatusCodes.Status400BadRequest);
        Guard.True(request.Ops.Count > 0, "INVALID_PATCH", "Patch operations are required.", StatusCodes.Status400BadRequest);
        Guard.True(request.Ops.Count <= MaxOpsPerPatch, "PATCH_TOO_LARGE", "Patch operation count exceeds limit.", StatusCodes.Status422UnprocessableEntity);

        var policyDefinition = policy.GetPolicy(request.PolicyId);
        var binding = ResolveBinding(policyDefinition, request.BindingId, key.Namespace, key.Path);
        ValidateWritablePaths(binding, request.Ops);

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

        ValidateEnvelope(binding, patchedEnvelope);

        var saved = await documentStore.UpsertAsync(key, patchedEnvelope, ifMatch, cancellationToken);
        var response = new MutationResponse(saved.ETag, saved.Envelope);

        await auditStore.WriteAsync(new AuditRecord(
            ChangeId: $"chg_{Guid.NewGuid():N}",
            Actor: actor,
            TenantId: key.TenantId,
            UserId: key.UserId,
            Namespace: key.Namespace,
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
        using var scope = BeginScope("document_replace", key.TenantId, key.UserId, key.Namespace, key.Path);
        logger.LogInformation("Replacing document.");

        Guard.True(!string.IsNullOrWhiteSpace(ifMatch), "MISSING_IF_MATCH", "If-Match header is required.", StatusCodes.Status400BadRequest);

        var policyDefinition = policy.GetPolicy(request.PolicyId);
        var binding = ResolveBinding(policyDefinition, request.BindingId, key.Namespace, key.Path);
        Guard.True(
            string.Equals(binding.WriteMode, "replace_allowed", StringComparison.OrdinalIgnoreCase),
            "REPLACE_NOT_ALLOWED",
            "Replace is not allowed for this binding.",
            StatusCodes.Status403Forbidden);

        var now = DateTimeOffset.UtcNow;
        var replacement = request.Document with { UpdatedAt = now, UpdatedBy = actor };
        ValidateEnvelope(binding, replacement);

        var saved = await documentStore.UpsertAsync(key, replacement, ifMatch, cancellationToken);
        var response = new MutationResponse(saved.ETag, saved.Envelope);

        await auditStore.WriteAsync(new AuditRecord(
            ChangeId: $"chg_{Guid.NewGuid():N}",
            Actor: actor,
            TenantId: key.TenantId,
            UserId: key.UserId,
            Namespace: key.Namespace,
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
        using var scope = BeginScope("context_assemble", tenantId, userId, policyId: request.PolicyId);
        logger.LogInformation("Assembling context.");

        var policyDefinition = policy.GetPolicy(request.PolicyId);
        var sortedBindings = policyDefinition.DocumentBindings.OrderBy(x => x.ReadPriority).ToList();

        var selectedProjectId = request.ConversationHint?.ProjectId;
        var deterministicScore = 0d;
        var reason = "no_match";
        if (string.IsNullOrWhiteSpace(selectedProjectId))
        {
            var routed = await TryRouteProjectAsync(tenantId, userId, policyDefinition, request.ConversationHint?.Text, cancellationToken);
            selectedProjectId = routed.ProjectId;
            deterministicScore = routed.Score;
            reason = routed.Reason;
        }
        else
        {
            deterministicScore = 1.0;
            reason = "explicit_project_id";
        }

        var maxDocs = request.MaxDocs.GetValueOrDefault(4);
        var maxCharsTotal = request.MaxCharsTotal.GetValueOrDefault(30000);

        var result = new List<AssembledDocument>();
        var dropped = new List<string>();
        var charBudgetUsed = 0;

        foreach (var binding in sortedBindings)
        {
            if (result.Count >= maxDocs)
            {
                dropped.Add(binding.BindingId);
                continue;
            }

            var resolvedPath = ResolveBindingPath(binding, selectedProjectId);
            if (resolvedPath is null)
            {
                continue;
            }

            var key = new DocumentKey(tenantId, userId, binding.Namespace, resolvedPath);
            var doc = await documentStore.GetAsync(key, cancellationToken);
            if (doc is null)
            {
                continue;
            }

            var chars = JsonSerializer.Serialize(doc.Envelope, JsonDefaults.Options).Length;
            if (charBudgetUsed + chars > maxCharsTotal)
            {
                dropped.Add(binding.BindingId);
                continue;
            }

            charBudgetUsed += chars;
            result.Add(new AssembledDocument(binding.BindingId, binding.Namespace, resolvedPath, doc.ETag, doc.Envelope));
        }

        return new AssembleContextResponse(
            SelectedProjectId: selectedProjectId,
            Documents: result,
            DroppedBindings: dropped,
            RoutingDebug: new RoutingDebug(deterministicScore, 0, reason));
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
        string? @namespace = null,
        string? path = null,
        string? policyId = null,
        string? eventId = null)
    {
        return logger.BeginScope(new Dictionary<string, object?>
        {
            ["operation"] = operation,
            ["tenant_id"] = tenantId,
            ["user_id"] = userId,
            ["namespace"] = @namespace,
            ["path"] = path,
            ["policy_id"] = policyId,
            ["event_id"] = eventId
        }) ?? NoopScope;
    }

    private sealed class ScopeHandle : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private void ValidateEnvelope(DocumentBinding binding, DocumentEnvelope envelope)
    {
        Guard.True(
            string.Equals(envelope.SchemaId, binding.SchemaId, StringComparison.Ordinal),
            "SCHEMA_MISMATCH",
            "Envelope schema_id does not match binding schema.",
            StatusCodes.Status422UnprocessableEntity);

        Guard.True(
            string.Equals(envelope.SchemaVersion, binding.SchemaVersion, StringComparison.Ordinal),
            "SCHEMA_VERSION_MISMATCH",
            "Envelope schema_version does not match binding schema version.",
            StatusCodes.Status422UnprocessableEntity);

        var serialized = JsonSerializer.Serialize(envelope, JsonDefaults.Options);
        Guard.True(serialized.Length <= binding.MaxChars, "DOCUMENT_SIZE_EXCEEDED", "Document exceeds max_chars for binding.", StatusCodes.Status422UnprocessableEntity);
        if (binding.MaxContentChars.HasValue)
        {
            var contentSerialized = JsonSerializer.Serialize(envelope.Content, JsonDefaults.Options);
            Guard.True(contentSerialized.Length <= binding.MaxContentChars.Value, "CONTENT_SIZE_EXCEEDED", "Document content exceeds binding max_content_chars.", StatusCodes.Status422UnprocessableEntity);
        }

        foreach (var requiredPath in binding.RequiredContentPaths)
        {
            if (!TryResolveContentPath(envelope.Content, requiredPath, out _))
            {
                throw new ApiException(StatusCodes.Status422UnprocessableEntity, "REQUIRED_PATH_MISSING", $"Required content path '{requiredPath}' was not found.");
            }
        }

        if (binding.MaxArrayItems.HasValue)
        {
            ValidateArrayLimits(envelope.Content, binding.MaxArrayItems.Value);
        }
    }

    private static void ValidateArrayLimits(JsonNode node, int maxArrayItems)
    {
        if (node is JsonArray array)
        {
            Guard.True(array.Count <= maxArrayItems, "ARRAY_LIMIT_EXCEEDED", "Array item count exceeds schema limit.", StatusCodes.Status422UnprocessableEntity);
            foreach (var child in array)
            {
                if (child is JsonNode childNode)
                {
                    ValidateArrayLimits(childNode, maxArrayItems);
                }
            }

            return;
        }

        if (node is JsonObject obj)
        {
            foreach (var child in obj)
            {
                if (child.Value is JsonNode childNode)
                {
                    ValidateArrayLimits(childNode, maxArrayItems);
                }
            }
        }
    }

    private static bool TryResolveContentPath(JsonObject content, string path, out JsonNode? node)
    {
        node = content;
        var tokens = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            if (node is JsonObject obj)
            {
                if (!obj.TryGetPropertyValue(token, out node))
                {
                    return false;
                }

                continue;
            }

            if (node is JsonArray array)
            {
                if (!int.TryParse(token, out var index) || index < 0 || index >= array.Count)
                {
                    return false;
                }

                node = array[index];
                continue;
            }

            return false;
        }

        return node is not null;
    }

    private static string NormalizePath(string path)
    {
        var trimmed = path.Trim();
        if (trimmed.StartsWith("/content", StringComparison.Ordinal))
        {
            trimmed = trimmed[8..];
            if (trimmed.Length == 0)
            {
                trimmed = "/";
            }
        }

        return trimmed;
    }

    private static DocumentBinding ResolveBinding(PolicyDefinition policyDefinition, string bindingId, string @namespace, string path)
    {
        var binding = policyDefinition.DocumentBindings.FirstOrDefault(x => string.Equals(x.BindingId, bindingId, StringComparison.Ordinal));
        Guard.NotNull(binding, "BINDING_NOT_FOUND", $"Binding '{bindingId}' not found in policy.", StatusCodes.Status422UnprocessableEntity);
        var resolvedBinding = binding!;

        Guard.True(string.Equals(resolvedBinding.Namespace, @namespace, StringComparison.Ordinal), "BINDING_NAMESPACE_MISMATCH", "Binding namespace does not match document namespace.", StatusCodes.Status422UnprocessableEntity);

        if (!string.IsNullOrWhiteSpace(resolvedBinding.Path))
        {
            Guard.True(string.Equals(resolvedBinding.Path, path, StringComparison.Ordinal), "BINDING_PATH_MISMATCH", "Binding path does not match document path.", StatusCodes.Status422UnprocessableEntity);
        }

        return resolvedBinding;
    }

    private static string? ResolveBindingPath(DocumentBinding binding, string? projectId)
    {
        if (!string.IsNullOrWhiteSpace(binding.Path))
        {
            return binding.Path;
        }

        if (string.IsNullOrWhiteSpace(binding.PathTemplate))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(projectId))
        {
            return null;
        }

        return binding.PathTemplate.Replace("{project_id}", projectId, StringComparison.Ordinal);
    }

    private static void ValidateWritablePaths(DocumentBinding binding, IReadOnlyList<PatchOperation> ops)
    {
        if (binding.AllowedPaths.Count == 0)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "WRITE_NOT_ALLOWED", $"No writable paths configured for binding '{binding.BindingId}'.");
        }

        foreach (var op in ops)
        {
            var normalizedPath = NormalizePath(op.Path);
            var match = binding.AllowedPaths.Any(allowedPath =>
            {
                var canonicalAllowed = allowedPath.StartsWith('/') ? allowedPath : "/" + allowedPath;
                return normalizedPath.Equals(canonicalAllowed, StringComparison.Ordinal)
                       || normalizedPath.StartsWith(canonicalAllowed + "/", StringComparison.Ordinal);
            });

            if (!match)
            {
                throw new ApiException(StatusCodes.Status422UnprocessableEntity, "PATH_NOT_WRITABLE", $"Patch path '{op.Path}' is not writable for binding '{binding.BindingId}'.");
            }
        }
    }

    private async Task<(string? ProjectId, double Score, string Reason)> TryRouteProjectAsync(
        string tenantId,
        string userId,
        PolicyDefinition policyDefinition,
        string? hintText,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(hintText))
        {
            return (null, 0, "no_hint");
        }

        var tokens = hintText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        var topProjectId = default(string);
        var topScore = 0.0;

        foreach (var binding in policyDefinition.DocumentBindings.Where(x => !string.IsNullOrWhiteSpace(x.Path)))
        {
            var key = new DocumentKey(tenantId, userId, binding.Namespace, binding.Path!);
            var doc = await documentStore.GetAsync(key, cancellationToken);
            if (doc?.Envelope.Content["projects_index"] is not JsonArray projectsIndex)
            {
                continue;
            }

            foreach (var node in projectsIndex)
            {
                if (node is not JsonObject item)
                {
                    continue;
                }

                var projectId = item["project_id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(projectId))
                {
                    continue;
                }

                var aliases = item["aliases"] is JsonArray aliasArray
                    ? aliasArray.Where(x => x is not null).Select(x => x!.GetValue<string>().ToLowerInvariant()).ToArray()
                    : Array.Empty<string>();

                var keywords = item["keywords"] is JsonArray keywordArray
                    ? keywordArray.Where(x => x is not null).Select(x => x!.GetValue<string>().ToLowerInvariant()).ToArray()
                    : Array.Empty<string>();

                var score = aliases.Count(alias => tokens.Contains(alias));
                score += keywords.Count(keyword => tokens.Contains(keyword));

                if (score > topScore)
                {
                    topScore = score;
                    topProjectId = projectId;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(topProjectId))
        {
            return (null, 0, "no_match");
        }

        return (topProjectId, Math.Min(1, topScore / 3.0), "alias_or_keyword_match");
    }

}
