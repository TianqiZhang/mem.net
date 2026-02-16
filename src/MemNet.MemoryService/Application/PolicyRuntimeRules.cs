using System.Text.Json;
using System.Text.Json.Nodes;
using MemNet.MemoryService.Infrastructure;
using Microsoft.AspNetCore.Http;

namespace MemNet.MemoryService.Core;

public sealed class PolicyRuntimeRules(PolicyRegistry policyRegistry)
{
    public DocumentBinding ResolveMutationBinding(DocumentKey key, string policyId, string bindingId)
    {
        var policyDefinition = policyRegistry.GetPolicy(policyId);
        return ResolveBinding(policyDefinition, bindingId, key.Namespace, key.Path);
    }

    public IReadOnlyList<DocumentBinding> ResolveAssembleBindings(string policyId)
    {
        var policyDefinition = policyRegistry.GetPolicy(policyId);
        return policyDefinition.DocumentBindings.OrderBy(x => x.ReadPriority).ToList();
    }

    public RetentionRules ResolveRetentionRules(string policyId)
    {
        var policyDefinition = policyRegistry.GetPolicy(policyId);
        return policyDefinition.RetentionRules;
    }

    public void EnsureReplaceAllowed(DocumentBinding binding)
    {
        Guard.True(
            string.Equals(binding.WriteMode, "replace_allowed", StringComparison.OrdinalIgnoreCase),
            "REPLACE_NOT_ALLOWED",
            "Replace is not allowed for this binding.",
            StatusCodes.Status403Forbidden);
    }

    public string? ResolveBindingPath(DocumentBinding binding)
    {
        if (!string.IsNullOrWhiteSpace(binding.Path))
        {
            return binding.Path;
        }

        return null;
    }

    public void ValidateWritablePaths(DocumentBinding binding, IReadOnlyList<PatchOperation> ops)
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

    public void ValidateEnvelope(DocumentBinding binding, DocumentEnvelope envelope)
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
}
