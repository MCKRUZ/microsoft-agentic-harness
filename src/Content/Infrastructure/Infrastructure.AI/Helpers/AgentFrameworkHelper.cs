using Azure.AI.OpenAI;
using OpenAI;

namespace Infrastructure.AI.Helpers;

/// <summary>
/// Provides pre-configured client options for AI framework SDK clients.
/// Centralizes timeout, retry, telemetry, and user-agent settings.
/// </summary>
/// <remarks>
/// Lives in Infrastructure.AI because it depends on external SDK types
/// (<see cref="AzureOpenAIClientOptions"/>, <see cref="OpenAIClientOptions"/>).
/// Consumed by <see cref="Factories.ChatClientFactory"/> and DI registration.
/// </remarks>
public static class AgentFrameworkHelper
{
    private const string UserAgentValue = "AgenticHarness/1.0";
    private const int DefaultNetworkTimeoutSeconds = 300;

    /// <summary>
    /// Gets configured options for <see cref="AzureOpenAIClient"/>.
    /// </summary>
    /// <param name="networkTimeoutSeconds">Network timeout in seconds. Default: 300.</param>
    /// <returns>Configured <see cref="AzureOpenAIClientOptions"/>.</returns>
    public static AzureOpenAIClientOptions GetAzureOpenAIClientOptions(int networkTimeoutSeconds = DefaultNetworkTimeoutSeconds)
    {
        return new AzureOpenAIClientOptions
        {
            NetworkTimeout = TimeSpan.FromSeconds(networkTimeoutSeconds),
            UserAgentApplicationId = UserAgentValue
        };
    }

    /// <summary>
    /// Gets configured options for <see cref="OpenAIClient"/>.
    /// </summary>
    /// <param name="networkTimeoutSeconds">Network timeout in seconds. Default: 300.</param>
    /// <returns>Configured <see cref="OpenAIClientOptions"/>.</returns>
    public static OpenAIClientOptions GetOpenAIClientOptions(int networkTimeoutSeconds = DefaultNetworkTimeoutSeconds)
    {
        return new OpenAIClientOptions
        {
            NetworkTimeout = TimeSpan.FromSeconds(networkTimeoutSeconds),
            UserAgentApplicationId = UserAgentValue
        };
    }
}
