using System.Text.Json.Serialization;

namespace Presentation.AgentHub.AgUi;

/// <summary>Signals that a Magentic workflow has started.</summary>
public sealed record MagenticWorkflowStartedEvent : AgUiEvent
{
    /// <summary>Identifier of the workflow run.</summary>
    [JsonPropertyName("workflowId")]
    public required string WorkflowId { get; init; }

    /// <summary>Human-readable workflow name.</summary>
    [JsonPropertyName("workflowName")]
    public required string WorkflowName { get; init; }

    /// <summary>Ids of the participant agents the manager may dispatch to.</summary>
    [JsonPropertyName("participants")]
    public required IReadOnlyList<string> Participants { get; init; }
}

/// <summary>Signals that the manager produced a plan, or revised it.</summary>
public sealed record MagenticPlanEvent : AgUiEvent
{
    /// <summary>Identifier of the workflow run.</summary>
    [JsonPropertyName("workflowId")]
    public required string WorkflowId { get; init; }

    /// <summary>1 for the initial plan; incremented on each replan.</summary>
    [JsonPropertyName("planVersion")]
    public required int PlanVersion { get; init; }

    /// <summary>The manager's plan, sanitized, redacted and bounded before it was sent.</summary>
    [JsonPropertyName("planText")]
    public required string PlanText { get; init; }
}

/// <summary>Signals that the manager completed a coordination round and chose who acts next.</summary>
public sealed record MagenticRoundEvent : AgUiEvent
{
    /// <summary>Identifier of the workflow run.</summary>
    [JsonPropertyName("workflowId")]
    public required string WorkflowId { get; init; }

    /// <summary>1-based round number.</summary>
    [JsonPropertyName("round")]
    public required int Round { get; init; }

    /// <summary>The participant the manager picked. Absent when it picked none.</summary>
    [JsonPropertyName("nextSpeaker")]
    public string? NextSpeaker { get; init; }

    /// <summary>What the manager asked that participant to do. Absent when none.</summary>
    [JsonPropertyName("instruction")]
    public string? Instruction { get; init; }

    /// <summary>Whether the manager judged the request answered.</summary>
    [JsonPropertyName("requestSatisfied")]
    public required bool RequestSatisfied { get; init; }

    /// <summary>Whether the manager judged the run to be repeating itself.</summary>
    [JsonPropertyName("inLoop")]
    public required bool InLoop { get; init; }

    /// <summary>Whether the manager judged the run to be making progress.</summary>
    [JsonPropertyName("progressing")]
    public required bool Progressing { get; init; }
}

/// <summary>Signals that the workflow is paused waiting for a human to review the plan.</summary>
public sealed record MagenticPlanReviewRequestedEvent : AgUiEvent
{
    /// <summary>Identifier of the workflow run.</summary>
    [JsonPropertyName("workflowId")]
    public required string WorkflowId { get; init; }

    /// <summary>The plan under review, sanitized, redacted and bounded before it was sent.</summary>
    [JsonPropertyName("planText")]
    public required string PlanText { get; init; }

    /// <summary>
    /// True when <see cref="PlanText"/> is not the whole plan. A reviewer can only approve what they were
    /// shown, so a client must say so rather than present a cut plan as complete.
    /// </summary>
    [JsonPropertyName("planTruncated")]
    public required bool PlanTruncated { get; init; }

    /// <summary>Whether the review was triggered by a stall rather than the initial plan.</summary>
    [JsonPropertyName("isStalled")]
    public required bool IsStalled { get; init; }
}

/// <summary>Signals that the workflow ended with a result (including a round or reset limit).</summary>
public sealed record MagenticWorkflowCompletedEvent : AgUiEvent
{
    /// <summary>Identifier of the workflow run.</summary>
    [JsonPropertyName("workflowId")]
    public required string WorkflowId { get; init; }

    /// <summary>Why it ended.</summary>
    [JsonPropertyName("completionReason")]
    public required string CompletionReason { get; init; }

    /// <summary>Coordination rounds the run used.</summary>
    [JsonPropertyName("roundsExecuted")]
    public required int RoundsExecuted { get; init; }
}

/// <summary>Signals that the workflow ended without a usable result.</summary>
public sealed record MagenticWorkflowFailedEvent : AgUiEvent
{
    /// <summary>Identifier of the workflow run.</summary>
    [JsonPropertyName("workflowId")]
    public required string WorkflowId { get; init; }

    /// <summary>A stable <c>magentic.*</c> code; never exception text.</summary>
    [JsonPropertyName("errorCode")]
    public required string ErrorCode { get; init; }
}
