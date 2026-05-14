namespace Domain.Common.Config.AI.Resilience;

/// <summary>
/// Declares what an LLM provider supports. Used by <c>ProviderCapabilityRegistry</c>
/// (Section 14) for capability diffing when falling back to a less capable provider.
/// </summary>
public class ProviderCapabilitiesConfig
{
    /// <summary>Whether the provider supports tool/function calling.</summary>
    public bool SupportsToolCalling { get; set; } = true;

    /// <summary>Whether the provider supports streaming responses.</summary>
    public bool SupportsStreaming { get; set; } = true;

    /// <summary>Whether the provider supports vision/image inputs.</summary>
    public bool SupportsVision { get; set; }

    /// <summary>Maximum tokens the provider can generate in a single response.</summary>
    public int MaxTokens { get; set; } = 4096;

    /// <summary>Media types the provider accepts (e.g., "image/png", "image/jpeg").</summary>
    public IReadOnlyList<string> SupportedMediaTypes { get; set; } = [];
}
