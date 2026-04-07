namespace Domain.Common.Config.Observability;

/// <summary>
/// Configuration for LLM cost estimation. Maps model names to per-token
/// pricing so the observability pipeline can compute estimated spend
/// from token usage telemetry.
/// </summary>
/// <remarks>
/// <para>
/// Pricing is expressed in USD per million tokens. The harness reads
/// <c>gen_ai.request.model</c> from completed spans and looks up the
/// matching <see cref="ModelPricingEntry"/> to compute cost.
/// </para>
/// <para>
/// Cache pricing supports the Anthropic prompt caching model where
/// cache writes cost more than regular input but cache reads are
/// significantly cheaper, providing cost savings on repeated prompts.
/// </para>
/// </remarks>
public class LlmPricingConfig
{
    /// <summary>
    /// Gets or sets the default model name used when a span does not
    /// include a <c>gen_ai.request.model</c> attribute.
    /// </summary>
    /// <value>Default: "claude-sonnet-4-6".</value>
    public string DefaultModel { get; set; } = "claude-sonnet-4-6";

    /// <summary>
    /// Gets or sets the per-model pricing entries.
    /// </summary>
    public List<ModelPricingEntry> Models { get; set; } =
    [
        new() { Name = "claude-opus-4-6", InputPerMillion = 15.00m, OutputPerMillion = 75.00m, CacheReadPerMillion = 1.50m, CacheWritePerMillion = 18.75m },
        new() { Name = "claude-sonnet-4-6", InputPerMillion = 3.00m, OutputPerMillion = 15.00m, CacheReadPerMillion = 0.30m, CacheWritePerMillion = 3.75m },
        new() { Name = "claude-haiku-4-5", InputPerMillion = 0.80m, OutputPerMillion = 4.00m, CacheReadPerMillion = 0.08m, CacheWritePerMillion = 1.00m },
    ];
}

/// <summary>
/// Pricing for a single LLM model, expressed in USD per million tokens.
/// </summary>
public class ModelPricingEntry
{
    /// <summary>
    /// Gets or sets the model identifier (e.g., "claude-opus-4-6").
    /// Matched against the <c>gen_ai.request.model</c> span attribute.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the cost per million input tokens in USD.</summary>
    public decimal InputPerMillion { get; set; }

    /// <summary>Gets or sets the cost per million output tokens in USD.</summary>
    public decimal OutputPerMillion { get; set; }

    /// <summary>Gets or sets the cost per million cache-read input tokens in USD.</summary>
    public decimal CacheReadPerMillion { get; set; }

    /// <summary>Gets or sets the cost per million cache-write input tokens in USD.</summary>
    public decimal CacheWritePerMillion { get; set; }
}
