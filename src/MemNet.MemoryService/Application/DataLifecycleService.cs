using MemNet.MemoryService.Infrastructure;
using Microsoft.AspNetCore.Http;

namespace MemNet.MemoryService.Core;

public sealed class DataLifecycleService(IUserDataMaintenanceStore maintenanceStore)
{
    public Task<ForgetUserResult> ForgetUserAsync(
        string tenantId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        return maintenanceStore.ForgetUserAsync(tenantId, userId, cancellationToken);
    }

    public Task<RetentionSweepResult> ApplyRetentionAsync(
        string tenantId,
        string userId,
        ApplyRetentionRequest request,
        CancellationToken cancellationToken = default)
    {
        Guard.True(request.EventsDays >= 0, "INVALID_RETENTION_VALUE", "events_days must be >= 0.", StatusCodes.Status400BadRequest);
        Guard.True(request.AuditDays >= 0, "INVALID_RETENTION_VALUE", "audit_days must be >= 0.", StatusCodes.Status400BadRequest);
        Guard.True(request.SnapshotsDays >= 0, "INVALID_RETENTION_VALUE", "snapshots_days must be >= 0.", StatusCodes.Status400BadRequest);

        var retentionRules = new RetentionRules(
            SnapshotsDays: request.SnapshotsDays,
            EventsDays: request.EventsDays,
            AuditDays: request.AuditDays);

        var now = (request.AsOfUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        return maintenanceStore.ApplyRetentionAsync(tenantId, userId, retentionRules, now, cancellationToken);
    }
}
