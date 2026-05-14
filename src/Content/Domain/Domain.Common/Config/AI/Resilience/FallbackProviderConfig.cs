namespace Domain.Common.Config.AI.Resilience;

/// <summary>
/// One entry in the resilience fallback chain. Maps directly to
/// <c>IChatClientFactory.GetChatClientAsync(clientType, deploymentId)</c>.
/// </summary>
public class FallbackProviderConfig
{
    /// <summary>
    /// Which provider SDK to use for this fallback entry.
    /// </summary>
    public AIAgentFrameworkClientType ClientType { get; set; } = AIAgentFrameworkClientType.AzureOpenAI;

    /// <summary>Model deployment name passed to the chat client factory.</summary>
    public string DeploymentId { get; set; } = "";

    /// <summary>Optional feature declarations for capability diffing.</summary>
    public ProviderCapabilitiesConfig Capabilities { get; set; } = new();
}
