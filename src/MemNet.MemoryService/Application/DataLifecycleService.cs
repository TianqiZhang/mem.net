using MemNet.MemoryService.Infrastructure;
using Microsoft.AspNetCore.Http;

namespace MemNet.MemoryService.Core;

public sealed class DataLifecycleService(
    IUserDataMaintenanceStore maintenanceStore,
    PolicyRuntimeRules policyRules)
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
        var hasPolicy = !string.IsNullOrWhiteSpace(request.PolicyId);
        var hasExplicit = request.EventsDays.HasValue || request.AuditDays.HasValue || request.SnapshotsDays.HasValue;
        Guard.True(
            hasPolicy || hasExplicit,
            "MISSING_RETENTION_RULES",
            "Provide policy_id (v1) or explicit retention day values (events_days, audit_days, snapshots_days).",
            StatusCodes.Status400BadRequest);
        Guard.True(
            !(hasPolicy && hasExplicit),
            "INVALID_RETENTION_MODE",
            "Cannot provide both policy_id and explicit retention day values.",
            StatusCodes.Status400BadRequest);

        RetentionRules retentionRules;
        if (hasPolicy)
        {
            retentionRules = policyRules.ResolveRetentionRules(request.PolicyId!);
        }
        else
        {
            Guard.True(
                request.EventsDays.HasValue && request.AuditDays.HasValue && request.SnapshotsDays.HasValue,
                "INCOMPLETE_RETENTION_RULES",
                "events_days, audit_days, and snapshots_days must all be provided in v2 mode.",
                StatusCodes.Status400BadRequest);
            var eventsDays = request.EventsDays.GetValueOrDefault();
            var auditDays = request.AuditDays.GetValueOrDefault();
            var snapshotsDays = request.SnapshotsDays.GetValueOrDefault();
            Guard.True(eventsDays >= 0, "INVALID_RETENTION_VALUE", "events_days must be >= 0.", StatusCodes.Status400BadRequest);
            Guard.True(auditDays >= 0, "INVALID_RETENTION_VALUE", "audit_days must be >= 0.", StatusCodes.Status400BadRequest);
            Guard.True(snapshotsDays >= 0, "INVALID_RETENTION_VALUE", "snapshots_days must be >= 0.", StatusCodes.Status400BadRequest);

            retentionRules = new RetentionRules(
                SnapshotsDays: snapshotsDays,
                EventsDays: eventsDays,
                AuditDays: auditDays);
        }

        var now = (request.AsOfUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        return maintenanceStore.ApplyRetentionAsync(tenantId, userId, retentionRules, now, cancellationToken);
    }
}
