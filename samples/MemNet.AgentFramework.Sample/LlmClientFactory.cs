using System.ClientModel;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;
using OpenAI;

internal static class LlmClientFactory
{
    public static IChatClient Create(SampleConfig config)
    {
        return config.UseAzureOpenAi
            ? CreateAzureOpenAiChatClient(config.AzureOpenAiEndpoint!, config.ModelName)
            : CreateOpenAiChatClient(config.ModelName);
    }

    private static IChatClient CreateOpenAiChatClient(string modelName)
    {
        return new OpenAIClient(RequireEnv("OPENAI_API_KEY"))
            .GetChatClient(modelName)
            .AsIChatClient();
    }

    private static IChatClient CreateAzureOpenAiChatClient(string endpoint, string deploymentName)
    {
        var apiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");

        var client = string.IsNullOrWhiteSpace(apiKey)
            ? new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
            : new AzureOpenAIClient(new Uri(endpoint), new ApiKeyCredential(apiKey));

        return client.GetChatClient(deploymentName).AsIChatClient();
    }

    private static string RequireEnv(string name)
    {
        return Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException($"Set environment variable '{name}'.");
    }
}
