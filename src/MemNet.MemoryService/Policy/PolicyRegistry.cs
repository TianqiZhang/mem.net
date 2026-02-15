using System.Text.Json;
using MemNet.MemoryService.Core;
using Microsoft.AspNetCore.Http;

namespace MemNet.MemoryService.Infrastructure;

public sealed class PolicyRegistry
{
    private readonly Dictionary<string, PolicyDefinition> _policies;

    public PolicyRegistry(StorageOptions options)
    {
        var policyPath = Path.Combine(options.ConfigRoot, "policy.json");
        var policy = JsonSerializer.Deserialize<PolicyConfig>(File.ReadAllText(policyPath), JsonDefaults.Options)
            ?? throw new InvalidOperationException("Failed to load policy configuration.");

        _policies = policy.Policies.ToDictionary(x => x.PolicyId, x => x, StringComparer.Ordinal);
    }

    public PolicyDefinition GetPolicy(string policyId)
    {
        if (_policies.TryGetValue(policyId, out var policy))
        {
            return policy;
        }

        throw new ApiException(StatusCodes.Status404NotFound, "POLICY_NOT_FOUND", $"Policy '{policyId}' was not found.");
    }
}
