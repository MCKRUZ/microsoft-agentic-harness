namespace Domain.AI.RAG.Models;

/// <summary>
/// Token usage and cost accounting for a retrieval execution or plan execution.
/// </summary>
public sealed record RetrievalCostSummary
{
    /// <summary>Total tokens consumed across all retrieval-related LLM calls.</summary>
    public required int TotalTokensUsed { get; init; }

    /// <summary>Prompt/input tokens sent to the LLM.</summary>
    public required int PromptTokens { get; init; }

    /// <summary>Completion/output tokens received from the LLM.</summary>
    public required int CompletionTokens { get; init; }

    /// <summary>Number of distinct retrieval API calls made.</summary>
    public required int RetrievalCalls { get; init; }

    /// <summary>Cumulative wall-clock time spent on retrieval operations.</summary>
    public required TimeSpan TotalLatency { get; init; }

    /// <summary>
    /// Estimated cost in USD based on token counts and configured per-token pricing.
    /// </summary>
    public required double EstimatedCost { get; init; }
}
