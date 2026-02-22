internal sealed record SampleConfig(
    string MemNetBaseUrl,
    string TenantId,
    string UserId,
    string ServiceId,
    bool EnableLearnMcp,
    string LearnMcpEndpoint,
    bool UseAzureOpenAi,
    string? AzureOpenAiEndpoint,
    string ModelName,
    string ProviderLabel)
{
    public static SampleConfig LoadFromEnvironment()
    {
        var azureOpenAiEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var useAzureOpenAi = !string.IsNullOrWhiteSpace(azureOpenAiEndpoint);
        var modelName = useAzureOpenAi
            ? Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_NAME") ?? "gpt-5.1"
            : Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-5.1";

        return new SampleConfig(
            MemNetBaseUrl: Environment.GetEnvironmentVariable("MEMNET_BASE_URL") ?? "http://localhost:5071",
            TenantId: Environment.GetEnvironmentVariable("MEMNET_TENANT_ID") ?? "tenant-demo",
            UserId: Environment.GetEnvironmentVariable("MEMNET_USER_ID") ?? "user-demo",
            ServiceId: Environment.GetEnvironmentVariable("MEMNET_SERVICE_ID") ?? "memory-agent-sample",
            EnableLearnMcp: GetBooleanEnv("MEMNET_ENABLE_LEARN_MCP", defaultValue: true),
            LearnMcpEndpoint: Environment.GetEnvironmentVariable("MEMNET_LEARN_MCP_ENDPOINT") ?? "https://learn.microsoft.com/api/mcp",
            UseAzureOpenAi: useAzureOpenAi,
            AzureOpenAiEndpoint: azureOpenAiEndpoint,
            ModelName: modelName,
            ProviderLabel: useAzureOpenAi ? "azure_openai" : "openai");
    }

    private static bool GetBooleanEnv(string name, bool defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        if (bool.TryParse(raw, out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException(
            $"Environment variable '{name}' must be 'true' or 'false' when set.");
    }
}
