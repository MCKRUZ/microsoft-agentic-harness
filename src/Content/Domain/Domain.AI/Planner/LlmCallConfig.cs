namespace Domain.AI.Planner;

/// <summary>
/// Configuration for an LLM inference step. Delegates execution to
/// <c>RunConversationCommand</c> with the specified model and prompt settings.
/// </summary>
public sealed record LlmCallConfig : StepConfiguration
{
    /// <summary>System prompt text sent to the LLM.</summary>
    public required string SystemPrompt { get; init; }

    /// <summary>References an AI deployment key from application configuration.</summary>
    public required string ModelDeploymentKey { get; init; }

    /// <summary>Sampling temperature controlling response randomness (0.0-2.0).</summary>
    public double Temperature { get; init; } = 0.7;

    /// <summary>Maximum tokens in the LLM response.</summary>
    public int MaxTokens { get; init; } = 4096;
}
