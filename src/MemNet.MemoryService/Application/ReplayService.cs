namespace MemNet.MemoryService.Core;

public sealed class ReplayService(MemoryCoordinator coordinator)
{
    public Task<MutationResponse> ApplyReplayPatchAsync(
        DocumentKey key,
        ReplayPatchRecord replay,
        string actor,
        CancellationToken cancellationToken = default)
    {
        var patchRequest = new PatchDocumentRequest(
            Ops: replay.Ops,
            Reason: "replay_update",
            Evidence: new EvidenceRef(
                ConversationId: null,
                MessageIds: replay.MessageIds,
                SnapshotUri: replay.SnapshotUri));

        return coordinator.PatchDocumentAsync(key, patchRequest, replay.BaseETag, actor, cancellationToken);
    }
}
