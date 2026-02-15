using MemNet.MemoryService.Infrastructure;

namespace MemNet.MemoryService.Core;

public sealed class DataLifecycleService(
    IUserDataMaintenanceStore maintenanceStore,
    IProfileRegistryProvider profileRegistry)
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
        string profileId,
        DateTimeOffset? asOfUtc,
        CancellationToken cancellationToken = default)
    {
        var profile = profileRegistry.GetProfile(profileId);
        var now = (asOfUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        return maintenanceStore.ApplyRetentionAsync(tenantId, userId, profile.RetentionRules, now, cancellationToken);
    }
}
