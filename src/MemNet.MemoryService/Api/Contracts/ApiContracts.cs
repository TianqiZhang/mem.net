using System.Text.Json.Nodes;

namespace MemNet.MemoryService.Core;

public sealed record PatchDocumentRequest(
    IReadOnlyList<PatchOperation> Ops,
    string Reason,
    JsonNode? Evidence,
    IReadOnlyList<TextPatchEdit>? Edits = null);

public sealed record TextPatchEdit(
    string OldText,
    string NewText,
    int? Occurrence);

public sealed record ReplaceDocumentRequest(
    DocumentEnvelope Document,
    string Reason,
    JsonNode? Evidence);

public sealed record AssembleContextRequest(
    IReadOnlyList<AssembleFileRef> Files,
    int? MaxDocs,
    int? MaxCharsTotal);

public sealed record AssembleFileRef(
    string Path);

public sealed record AssembleContextResponse(
    IReadOnlyList<AssembledFile> Files,
    IReadOnlyList<DroppedFile> DroppedFiles);

public sealed record AssembledFile(
    string Path,
    string ETag,
    DocumentEnvelope Document);

public sealed record DroppedFile(
    string Path,
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
    int EventsDays,
    int AuditDays,
    int SnapshotsDays,
    DateTimeOffset? AsOfUtc);

public sealed record ErrorEnvelope(ApiError Error);
