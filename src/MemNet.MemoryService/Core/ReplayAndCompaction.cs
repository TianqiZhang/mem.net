using System.Text.Json;
using System.Text.Json.Nodes;
using MemNet.MemoryService.Infrastructure;

namespace MemNet.MemoryService.Core;

public sealed class ReplayService(MemoryCoordinator coordinator)
{
    public Task<MutationResponse> ApplyReplayPatchAsync(
        DocumentKey key,
        string profileId,
        ReplayPatchRecord replay,
        string actor,
        CancellationToken cancellationToken = default)
    {
        var patchRequest = new PatchDocumentRequest(
            ProfileId: profileId,
            BindingId: replay.TargetBindingId,
            Ops: replay.Ops,
            Reason: "replay_update",
            Evidence: new EvidenceRef(
                ConversationId: null,
                MessageIds: replay.MessageIds,
                SnapshotUri: replay.SnapshotUri),
            Confidence: replay.Confidence);

        return coordinator.PatchDocumentAsync(key, patchRequest, replay.BaseETag, actor, cancellationToken);
    }
}

public sealed class CompactionService(
    IDocumentStore documentStore,
    IProfileRegistryProvider profileRegistry)
{
    public async Task<bool> CompactBindingAsync(
        string tenantId,
        string userId,
        string profileId,
        string bindingId,
        string actor,
        CancellationToken cancellationToken = default)
    {
        var profile = profileRegistry.GetProfile(profileId);
        var binding = profile.DocumentBindings.FirstOrDefault(x => x.BindingId == bindingId);
        if (binding is null || string.IsNullOrWhiteSpace(binding.Path))
        {
            return false;
        }

        if (!profile.CompactionRules.TryGetValue(bindingId, out var rule))
        {
            return false;
        }

        var key = new DocumentKey(tenantId, userId, binding.Namespace, binding.Path);
        var current = await documentStore.GetAsync(key, cancellationToken);
        if (current is null)
        {
            return false;
        }

        var changed = false;
        var content = (JsonObject?)current.Envelope.Content.DeepClone() ?? new JsonObject();

        changed |= TrimArray(content, "preferences", rule.MaxPreferences);
        changed |= TrimArray(content, "durable_facts", rule.MaxDurableFacts);
        changed |= TrimArray(content, "pending_confirmations", rule.MaxPendingConfirmations);
        changed |= TrimArray(content, "recent_notes", rule.MaxRecentNotes);

        if (!changed)
        {
            return false;
        }

        var updated = current.Envelope with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = actor,
            Content = content
        };

        var size = JsonSerializer.Serialize(updated, JsonDefaults.Options).Length;
        if (size > binding.MaxChars)
        {
            return false;
        }

        await documentStore.UpsertAsync(key, updated, current.ETag, cancellationToken);
        return true;
    }

    private static bool TrimArray(JsonObject content, string key, int? max)
    {
        if (!max.HasValue || max <= 0)
        {
            return false;
        }

        if (content[key] is not JsonArray array || array.Count <= max.Value)
        {
            return false;
        }

        var keep = array.Skip(array.Count - max.Value).Select(x => x?.DeepClone()).ToList();
        content[key] = new JsonArray(keep.ToArray());
        return true;
    }
}
