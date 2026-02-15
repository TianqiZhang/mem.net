using MemNet.MemoryService.Core;

namespace MemNet.MemoryService.Infrastructure;

public interface IDocumentStore
{
    Task<DocumentRecord?> GetAsync(DocumentKey key, CancellationToken cancellationToken = default);

    Task<DocumentRecord> UpsertAsync(
        DocumentKey key,
        DocumentEnvelope envelope,
        string? ifMatch,
        CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(DocumentKey key, CancellationToken cancellationToken = default);
}

public interface IEventStore
{
    Task WriteAsync(EventDigest digest, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EventDigest>> QueryAsync(
        string tenantId,
        string userId,
        EventSearchRequest request,
        CancellationToken cancellationToken = default);
}

public interface IAuditStore
{
    Task WriteAsync(AuditRecord record, CancellationToken cancellationToken = default);
}

public interface IUserDataMaintenanceStore
{
    Task<ForgetUserResult> ForgetUserAsync(string tenantId, string userId, CancellationToken cancellationToken = default);

    Task<RetentionSweepResult> ApplyRetentionAsync(
        string tenantId,
        string userId,
        RetentionRules rules,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default);
}

public interface IProfileRegistryProvider
{
    ProfileConfig GetProfile(string profileId);
}

public interface ISchemaRegistryProvider
{
    SchemaConfig GetSchema(string schemaId, string version);
}

public sealed record EventSearchRequest(
    string? Query,
    string? ServiceId,
    string? SourceType,
    string? ProjectId,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int TopK = 10);

public sealed record AuditRecord(
    string ChangeId,
    string Actor,
    string TenantId,
    string UserId,
    string Namespace,
    string Path,
    string? PreviousETag,
    string NewETag,
    string Reason,
    IReadOnlyList<PatchOperation> Ops,
    DateTimeOffset Timestamp,
    IReadOnlyList<string>? EvidenceMessageIds);
