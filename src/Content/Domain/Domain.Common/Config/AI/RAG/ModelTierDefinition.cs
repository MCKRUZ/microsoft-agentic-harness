namespace Domain.Common.Config.AI.RAG;

/// <summary>
/// Defines a model tier mapping a logical tier name to a specific
/// Azure OpenAI deployment with rate limits.
/// Bound from <c>AppConfig:AI:Rag:ModelTiering:Tiers[]</c> in appsettings.json.
/// </summary>
public class ModelTierDefinition
{
    /// <summary>
    /// Gets or sets the logical tier name (e.g., <c>"premium"</c>,
    /// <c>"standard"</c>, <c>"economy"</c>).
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Gets or sets the Azure OpenAI deployment name associated with this tier.
    /// </summary>
    public string DeploymentName { get; set; } = "";

    /// <summary>
    /// Gets or sets the maximum tokens per minute allowed for this tier's
    /// deployment. Used for rate limiting and load balancing.
    /// </summary>
    public int MaxTokensPerMinute { get; set; } = 100_000;
}
