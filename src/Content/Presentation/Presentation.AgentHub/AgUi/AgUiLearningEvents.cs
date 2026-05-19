using System.Text.Json.Serialization;

namespace Presentation.AgentHub.AgUi;

/// <summary>
/// Signals that the agent has captured a new learning. Emitted after a
/// <c>RememberCommand</c> successfully persists a learning entry.
/// </summary>
public sealed record LearningCapturedEvent : AgUiEvent
{
    /// <summary>Unique identifier for the learning entry.</summary>
    [JsonPropertyName("learningId")]
    public required string LearningId { get; init; }

    /// <summary>Learning category (e.g. "FactualCorrection", "StylePreference").</summary>
    [JsonPropertyName("category")]
    public required string Category { get; init; }

    /// <summary>Agent ID this learning is scoped to, if any.</summary>
    [JsonPropertyName("agentId")]
    public string? AgentId { get; init; }

    /// <summary>Team ID this learning is scoped to, if any.</summary>
    [JsonPropertyName("teamId")]
    public string? TeamId { get; init; }

    /// <summary>Whether this is a global learning.</summary>
    [JsonPropertyName("isGlobal")]
    public required bool IsGlobal { get; init; }

    /// <summary>Human-readable description of the learning source.</summary>
    [JsonPropertyName("sourceDescription")]
    public required string SourceDescription { get; init; }
}

/// <summary>
/// Signals that a previously captured learning was applied during agent execution.
/// The learning's content influenced the agent's response or tool usage.
/// </summary>
public sealed record LearningAppliedEvent : AgUiEvent
{
    /// <summary>The learning that was applied.</summary>
    [JsonPropertyName("learningId")]
    public required string LearningId { get; init; }

    /// <summary>The agent that applied the learning.</summary>
    [JsonPropertyName("agentId")]
    public required string AgentId { get; init; }

    /// <summary>Learning category for display.</summary>
    [JsonPropertyName("category")]
    public required string Category { get; init; }

    /// <summary>Optional summary of the context in which the learning was applied.</summary>
    [JsonPropertyName("contextSummary")]
    public string? ContextSummary { get; init; }
}

/// <summary>
/// Signals that a learning has been forgotten (soft-deleted) and will no longer
/// influence future agent behavior.
/// </summary>
public sealed record LearningForgottenEvent : AgUiEvent
{
    /// <summary>The learning that was forgotten.</summary>
    [JsonPropertyName("learningId")]
    public required string LearningId { get; init; }

    /// <summary>Reason for forgetting this learning.</summary>
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}
