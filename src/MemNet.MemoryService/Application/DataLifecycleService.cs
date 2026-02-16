using MemNet.MemoryService.Infrastructure;

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
        string policyId,
        DateTimeOffset? asOfUtc,
        CancellationToken cancellationToken = default)
    {
        var retentionRules = policyRules.ResolveRetentionRules(policyId);
        var now = (asOfUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        return maintenanceStore.ApplyRetentionAsync(tenantId, userId, retentionRules, now, cancellationToken);
    }
}
