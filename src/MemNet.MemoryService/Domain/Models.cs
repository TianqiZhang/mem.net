using System.Text.Json.Nodes;

namespace MemNet.MemoryService.Core;

public sealed record DocumentKey(string TenantId, string UserId, string Path);

public sealed record DocumentEnvelope(
    string DocId,
    string SchemaId,
    string SchemaVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string UpdatedBy,
    JsonObject Content);

public sealed record DocumentRecord(DocumentEnvelope Envelope, string ETag);

public sealed record FileListItem(string Path, DateTimeOffset LastModifiedUtc);

public sealed record PatchOperation(string Op, string Path, JsonNode? Value);

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

public sealed record ReplayPatchRecord(
    string ReplayId,
    string TargetBindingId,
    string TargetPath,
    string BaseETag,
    IReadOnlyList<PatchOperation> Ops,
    JsonNode? Evidence);

public sealed record RetentionRules(int SnapshotsDays, int EventsDays, int AuditDays);

public sealed record MutationResponse(string ETag, DocumentEnvelope Document);

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
