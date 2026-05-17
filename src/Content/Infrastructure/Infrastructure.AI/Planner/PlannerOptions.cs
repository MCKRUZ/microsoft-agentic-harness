using Domain.Common.Config.AI;

namespace Infrastructure.AI.Planner;

/// <summary>
/// Configuration options for the LLM-based plan generator service.
/// Bound from the <c>Planner</c> configuration section.
/// </summary>
public sealed class PlannerOptions
{
    /// <summary>Configuration section path.</summary>
    public const string SectionName = "Planner";

    /// <summary>Model deployment key used for plan generation (e.g., "gpt-4o").</summary>
    public string GenerationModel { get; init; } = "gpt-4o";

    /// <summary>AI framework client type for the generation model.</summary>
    public AIAgentFrameworkClientType ClientType { get; init; } = AIAgentFrameworkClientType.AzureOpenAI;

    /// <summary>Sampling temperature for plan generation. Lower values produce more deterministic plans.</summary>
    public double GenerationTemperature { get; init; } = 0.3;

    /// <summary>Maximum response tokens for the generation request.</summary>
    public int GenerationMaxTokens { get; init; } = 4096;
}
