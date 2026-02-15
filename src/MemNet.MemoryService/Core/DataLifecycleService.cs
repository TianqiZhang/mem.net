using MemNet.MemoryService.Infrastructure;

namespace MemNet.MemoryService.Core;

public sealed class DataLifecycleService(
    IUserDataMaintenanceStore maintenanceStore,
    PolicyRegistry policy)
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
        var policyDefinition = policy.GetPolicy(policyId);
        var now = (asOfUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        return maintenanceStore.ApplyRetentionAsync(tenantId, userId, policyDefinition.RetentionRules, now, cancellationToken);
    }
}
