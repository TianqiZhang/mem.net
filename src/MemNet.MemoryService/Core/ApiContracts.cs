using System.Text.Json.Nodes;

namespace MemNet.MemoryService.Core;

public sealed record PatchDocumentRequest(
    string PolicyId,
    string BindingId,
    IReadOnlyList<PatchOperation> Ops,
    string Reason,
    EvidenceRef? Evidence);

public sealed record ReplaceDocumentRequest(
    string PolicyId,
    string BindingId,
    DocumentEnvelope Document,
    string Reason,
    EvidenceRef? Evidence);

public sealed record EvidenceRef(
    string? ConversationId,
    IReadOnlyList<string>? MessageIds,
    string? SnapshotUri);

public sealed record AssembleContextRequest(
    string PolicyId,
    ConversationHint? ConversationHint,
    int? MaxDocs,
    int? MaxCharsTotal);

public sealed record ConversationHint(string? Text, string? ProjectId);

public sealed record AssembleContextResponse(
    string? SelectedProjectId,
    IReadOnlyList<AssembledDocument> Documents,
    IReadOnlyList<string> DroppedBindings,
    RoutingDebug RoutingDebug);

public sealed record AssembledDocument(
    string BindingId,
    string Namespace,
    string Path,
    string ETag,
    DocumentEnvelope Document);

public sealed record RoutingDebug(
    double DeterministicScore,
    double SemanticScore,
    string Reason);

public sealed record SearchEventsRequest(
    string? Query,
    string? ServiceId,
    string? SourceType,
    string? ProjectId,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int? TopK);

public sealed record SearchEventsResponse(IReadOnlyList<EventDigest> Results);

public sealed record WriteEventRequest(EventDigest Event);

public sealed record ApplyRetentionRequest(
    string PolicyId,
    DateTimeOffset? AsOfUtc);

public sealed record ErrorEnvelope(ApiError Error);
