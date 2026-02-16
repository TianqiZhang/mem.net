using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MemNet.AgentMemory;

public sealed class AgentMemory
{
    private readonly MemNet.Client.MemNetClient _client;
    private readonly AgentMemoryPolicy _policy;
    private readonly AgentMemoryOptions _options;
    private readonly Dictionary<string, MemorySlotPolicy> _slots;

    public AgentMemory(MemNet.Client.MemNetClient client, AgentMemoryPolicy policy, AgentMemoryOptions? options = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _options = options ?? new AgentMemoryOptions();
        _slots = policy.Slots.ToDictionary(x => x.SlotId, x => x, StringComparer.Ordinal);
    }

    public async Task<PreparedMemory> PrepareTurnAsync(MemNet.Client.MemNetScope scope, PrepareTurnRequest request, CancellationToken cancellationToken = default)
    {
        var selectedSlotIds = _policy.Slots
            .Where(x => x.LoadByDefault)
            .Select(x => x.SlotId)
            .Concat(request.AdditionalSlotIds ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var assembledDocs = new List<PreparedSlotDocument>();
        var dropped = new List<MemNet.Client.DroppedDocument>();

        if (selectedSlotIds.Length > 0)
        {
            var refMap = new Dictionary<string, string>(StringComparer.Ordinal);
            var refs = new List<MemNet.Client.AssembleDocumentRef>(selectedSlotIds.Length);
            foreach (var slotId in selectedSlotIds)
            {
                var resolved = ResolveSlot(slotId, request.TemplateVariables);
                refs.Add(new MemNet.Client.AssembleDocumentRef(resolved.Document.Namespace, resolved.Document.Path));
                refMap[$"{resolved.Document.Namespace}|{resolved.Document.Path}"] = slotId;
            }

            var assembled = await _client.AssembleContextAsync(
                scope,
                new MemNet.Client.AssembleContextRequest(
                    Documents: refs,
                    MaxDocs: request.MaxDocs ?? _options.DefaultAssembleMaxDocs,
                    MaxCharsTotal: request.MaxCharsTotal ?? _options.DefaultAssembleMaxCharsTotal),
                cancellationToken);

            foreach (var doc in assembled.Documents)
            {
                refMap.TryGetValue($"{doc.Namespace}|{doc.Path}", out var slotId);
                assembledDocs.Add(new PreparedSlotDocument(
                    SlotId: slotId,
                    Document: new MemNet.Client.DocumentRef(doc.Namespace, doc.Path),
                    ETag: doc.ETag,
                    Envelope: doc.Document));
            }

            dropped.AddRange(assembled.DroppedDocuments);
        }

        var events = Array.Empty<MemNet.Client.EventDigest>();
        if (!string.IsNullOrWhiteSpace(request.RecallQuery))
        {
            var recalled = await RecallAsync(
                scope,
                new RecallRequest(
                    Query: request.RecallQuery,
                    TopK: request.RecallTopK,
                    ServiceId: request.ServiceId,
                    SourceType: request.SourceType,
                    ProjectId: request.ProjectId,
                    From: request.From,
                    To: request.To),
                cancellationToken);
            events = recalled.ToArray();
        }

        return new PreparedMemory(assembledDocs, events, dropped);
    }

    public async Task<PreparedSlotDocument> LoadSlotAsync(
        MemNet.Client.MemNetScope scope,
        string slotId,
        IReadOnlyDictionary<string, string>? templateVariables = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = ResolveSlot(slotId, templateVariables);
        var doc = await _client.GetDocumentAsync(scope, resolved.Document, cancellationToken);
        return new PreparedSlotDocument(resolved.Slot.SlotId, resolved.Document, doc.ETag, doc.Document);
    }

    public async Task<MemNet.Client.DocumentMutationResult> PatchSlotAsync(
        MemNet.Client.MemNetScope scope,
        string slotId,
        SlotPatchRequest request,
        string ifMatch,
        string? serviceId = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = ResolveSlot(slotId, request.TemplateVariables);
        ValidatePatchRules(resolved.Slot, request.Ops);

        return await _client.PatchDocumentAsync(
            scope,
            resolved.Document,
            new MemNet.Client.PatchDocumentRequest(request.Ops, request.Reason, request.Evidence),
            ifMatch,
            serviceId,
            cancellationToken);
    }

    public async Task<MemNet.Client.DocumentMutationResult> ReplaceSlotAsync(
        MemNet.Client.MemNetScope scope,
        string slotId,
        SlotReplaceRequest request,
        string ifMatch,
        string? serviceId = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = ResolveSlot(slotId, request.TemplateVariables);
        ValidateReplaceRules(resolved.Slot, request.Document.Content);

        return await _client.ReplaceDocumentAsync(
            scope,
            resolved.Document,
            new MemNet.Client.ReplaceDocumentRequest(request.Document, request.Reason, request.Evidence),
            ifMatch,
            serviceId,
            cancellationToken);
    }

    public Task RememberAsync(MemNet.Client.MemNetScope scope, RememberRequest request, CancellationToken cancellationToken = default)
    {
        return _client.WriteEventAsync(scope, new MemNet.Client.WriteEventRequest(request.Event), cancellationToken);
    }

    public async Task<IReadOnlyList<MemNet.Client.EventDigest>> RecallAsync(
        MemNet.Client.MemNetScope scope,
        RecallRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _client.SearchEventsAsync(
            scope,
            new MemNet.Client.SearchEventsRequest(
                Query: request.Query,
                ServiceId: request.ServiceId,
                SourceType: request.SourceType,
                ProjectId: request.ProjectId,
                From: request.From,
                To: request.To,
                TopK: request.TopK ?? _options.DefaultRecallTopK),
            cancellationToken);

        return response.Results;
    }

    public Task<MemNet.Client.ForgetUserResult> ForgetUserAsync(MemNet.Client.MemNetScope scope, CancellationToken cancellationToken = default)
    {
        return _client.ForgetUserAsync(scope, cancellationToken);
    }

    private (MemorySlotPolicy Slot, MemNet.Client.DocumentRef Document) ResolveSlot(
        string slotId,
        IReadOnlyDictionary<string, string>? templateVariables)
    {
        if (!_slots.TryGetValue(slotId, out var slot))
        {
            throw new MemNet.Client.MemNetException($"Unknown slot '{slotId}'.");
        }

        var path = ResolveSlotPath(slot, templateVariables);
        return (slot, new MemNet.Client.DocumentRef(slot.Namespace, path));
    }

    private static string ResolveSlotPath(MemorySlotPolicy slot, IReadOnlyDictionary<string, string>? templateVariables)
    {
        if (!string.IsNullOrWhiteSpace(slot.Path))
        {
            return slot.Path;
        }

        if (string.IsNullOrWhiteSpace(slot.PathTemplate))
        {
            throw new MemNet.Client.MemNetException($"Slot '{slot.SlotId}' must define either path or path_template.");
        }

        var variables = templateVariables ?? new Dictionary<string, string>(StringComparer.Ordinal);
        return Regex.Replace(slot.PathTemplate, "\\{([a-zA-Z0-9_]+)\\}", match =>
        {
            var key = match.Groups[1].Value;
            if (!variables.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                throw new MemNet.Client.MemNetException($"Slot '{slot.SlotId}' requires template variable '{key}'.");
            }

            return value;
        });
    }

    private static void ValidatePatchRules(MemorySlotPolicy slot, IReadOnlyList<MemNet.Client.PatchOperation> ops)
    {
        var rules = slot.PatchRules;
        var allowed = rules?.AllowedPaths;
        if (allowed is null || allowed.Count == 0)
        {
            return;
        }

        foreach (var op in ops)
        {
            var normalized = NormalizePatchPath(op.Path);
            var matches = allowed.Any(x =>
            {
                var canonical = x.StartsWith('/') ? x : "/" + x;
                return normalized.Equals(canonical, StringComparison.Ordinal)
                       || normalized.StartsWith(canonical + "/", StringComparison.Ordinal);
            });

            if (!matches)
            {
                throw new MemNet.Client.MemNetException($"Patch path '{op.Path}' is not allowed for slot '{slot.SlotId}'.");
            }
        }
    }

    private static void ValidateReplaceRules(MemorySlotPolicy slot, JsonObject content)
    {
        var rules = slot.PatchRules;
        if (rules is null)
        {
            return;
        }

        if (rules.MaxContentChars.HasValue)
        {
            var json = JsonSerializer.Serialize(content);
            if (json.Length > rules.MaxContentChars.Value)
            {
                throw new MemNet.Client.MemNetException($"Content size exceeds max_content_chars for slot '{slot.SlotId}'.");
            }
        }

        if (rules.RequiredContentPaths is { Count: > 0 })
        {
            foreach (var path in rules.RequiredContentPaths)
            {
                if (!TryResolveContentPath(content, path, out _))
                {
                    throw new MemNet.Client.MemNetException($"Required content path '{path}' missing for slot '{slot.SlotId}'.");
                }
            }
        }

        if (rules.MaxArrayItems.HasValue)
        {
            ValidateArrayLimits(content, rules.MaxArrayItems.Value, slot.SlotId);
        }
    }

    private static void ValidateArrayLimits(JsonNode node, int maxArrayItems, string slotId)
    {
        if (node is JsonArray array)
        {
            if (array.Count > maxArrayItems)
            {
                throw new MemNet.Client.MemNetException($"Array item limit exceeded for slot '{slotId}'.");
            }

            foreach (var child in array)
            {
                if (child is JsonNode childNode)
                {
                    ValidateArrayLimits(childNode, maxArrayItems, slotId);
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
                    ValidateArrayLimits(childNode, maxArrayItems, slotId);
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

    private static string NormalizePatchPath(string path)
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
