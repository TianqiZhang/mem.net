using System.Net;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MemNet.Client;

public sealed record MemNetScope(string TenantId, string UserId);

public sealed record FileRef(string Path);

public sealed record ServiceStatusResponse(string Service, string Status);

public sealed record DocumentEnvelope(
    string DocId,
    string SchemaId,
    string SchemaVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string UpdatedBy,
    JsonObject Content);

public sealed record PatchOperation(string Op, string Path, JsonNode? Value);

public sealed record TextPatchEdit(
    string OldText,
    string NewText,
    int? Occurrence = null);

public sealed record PatchDocumentRequest(
    IReadOnlyList<PatchOperation> Ops,
    string Reason,
    JsonNode? Evidence = null,
    IReadOnlyList<TextPatchEdit>? Edits = null);

public sealed record ReplaceDocumentRequest(
    DocumentEnvelope Document,
    string Reason,
    JsonNode? Evidence = null);

public sealed record FileMutationResult(
    [property: JsonPropertyName("etag")]
    string ETag,
    DocumentEnvelope Document);

public sealed record FileReadResult(
    [property: JsonPropertyName("etag")]
    string ETag,
    DocumentEnvelope Document);

public sealed record AssembleContextRequest(
    IReadOnlyList<AssembleFileRef> Files,
    int? MaxDocs = null,
    int? MaxCharsTotal = null);

public sealed record AssembleFileRef(
    string Path);

public sealed record DroppedFile(
    string Path,
    string Reason);

public sealed record AssembledFile(
    string Path,
    [property: JsonPropertyName("etag")]
    string ETag,
    DocumentEnvelope Document);

public sealed record AssembleContextResponse(
    IReadOnlyList<AssembledFile> Files,
    IReadOnlyList<DroppedFile> DroppedFiles);

public sealed record EventDigest(
    string EventId,
    string TenantId,
    string UserId,
    string ServiceId,
    DateTimeOffset Timestamp,
    string SourceType,
    string Digest,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> ProjectIds,
    JsonNode? Evidence);

public sealed record WriteEventRequest(EventDigest Event);

public sealed record SearchEventsRequest(
    string? Query,
    string? ServiceId,
    string? SourceType,
    string? ProjectId,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int? TopK);

public sealed record SearchEventsResponse(IReadOnlyList<EventDigest> Results);

public sealed record ApplyRetentionRequest(
    int EventsDays,
    int AuditDays,
    int SnapshotsDays,
    DateTimeOffset? AsOfUtc = null);

public sealed record ForgetUserResult(
    int DocumentsDeleted,
    int EventsDeleted,
    int AuditDeleted,
    int SnapshotsDeleted,
    int SearchDocumentsDeleted);

public sealed record RetentionSweepResult(
    int EventsDeleted,
    int AuditDeleted,
    int SnapshotsDeleted,
    int SearchDocumentsDeleted,
    DateTimeOffset CutoffEventsUtc,
    DateTimeOffset CutoffAuditUtc,
    DateTimeOffset CutoffSnapshotsUtc);

public sealed record ApiError(
    string Code,
    string Message,
    string RequestId,
    IReadOnlyDictionary<string, string>? Details = null);

public sealed record ApiErrorEnvelope(ApiError Error);

public sealed record MemNetApiError(
    HttpStatusCode StatusCode,
    string Code,
    string Message,
    string? RequestId,
    IReadOnlyDictionary<string, string>? Details,
    string? RawResponseBody);
