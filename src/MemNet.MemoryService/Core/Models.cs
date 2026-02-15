using System.Text.Json.Nodes;

namespace MemNet.MemoryService.Core;

public sealed record DocumentKey(string TenantId, string UserId, string Namespace, string Path);

public sealed record DocumentEnvelope(
    string DocId,
    string SchemaId,
    string SchemaVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string UpdatedBy,
    JsonObject Content);

public sealed record DocumentRecord(DocumentEnvelope Envelope, string ETag);

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
    string SnapshotUri,
    EventEvidence Evidence);

public sealed record EventEvidence(
    IReadOnlyList<string> MessageIds,
    int? Start,
    int? End);

public sealed record ReplayPatchRecord(
    string ReplayId,
    string TargetBindingId,
    string TargetPath,
    string BaseETag,
    IReadOnlyList<PatchOperation> Ops,
    double Confidence,
    string SnapshotUri,
    IReadOnlyList<string> MessageIds,
    string IdempotencyKey);

public sealed record ProfileConfig(
    string ProfileId,
    IReadOnlyList<DocumentBinding> DocumentBindings,
    IReadOnlyDictionary<string, IReadOnlyList<string>> WritablePathRules,
    RetentionRules RetentionRules,
    ConfidenceRules ConfidenceRules,
    IReadOnlyDictionary<string, CompactionRule> CompactionRules);

public sealed record DocumentBinding(
    string BindingId,
    string Namespace,
    string? Path,
    string? PathTemplate,
    string SchemaId,
    string SchemaVersion,
    int MaxChars,
    int ReadPriority,
    string WriteMode);

public sealed record RetentionRules(int SnapshotsDays, int EventsDays, int AuditDays);

public sealed record ConfidenceRules(double MinConfidenceForDurableFact, double MinConfidenceForAutoApply);

public sealed record CompactionRule(
    int? MaxPreferences,
    int? MaxDurableFacts,
    int? MaxPendingConfirmations,
    int? MaxRecentNotes);

public sealed record SchemaConfig(
    string SchemaId,
    string Version,
    IReadOnlyList<string> RequiredContentPaths,
    int? MaxContentChars,
    int? MaxArrayItems);

public sealed record SchemaRegistry(IReadOnlyList<SchemaConfig> Schemas);

public sealed record ProfileRegistry(IReadOnlyList<ProfileConfig> Profiles);

public sealed record IdempotencyResult(
    IdempotencyState State,
    MutationResponse? ExistingResponse,
    string? ExistingPayloadHash);

public enum IdempotencyState
{
    Started,
    Replayed,
    Conflict
}

public sealed record MutationResponse(string ETag, DocumentEnvelope Document, bool IdempotencyReplay = false);

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
