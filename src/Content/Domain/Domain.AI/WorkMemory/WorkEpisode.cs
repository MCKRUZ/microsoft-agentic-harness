namespace Domain.AI.WorkMemory;

/// <summary>
/// A record of what the agent <em>did</em> on a single conversation turn — the unit of "work memory".
/// Unlike a <see cref="Domain.AI.Learnings.LearningEntry"/> (an isolated fact) or a cross-session
/// memory node (a user/domain fact), a work episode captures a <strong>trajectory</strong>: the task
/// the user posed, the response produced, whether it succeeded, and what it cost.
/// </summary>
/// <remarks>
/// <para>
/// Episodes are captured cheaply and structurally at the end of each turn by
/// <c>WorkEpisodeCaptureBehavior</c> — there is deliberately <strong>no LLM call</strong> on the
/// capture path. The expensive distillation of episodes into reusable lessons happens later, offline,
/// in the overnight synthesis pass (mirroring Perplexity Brain's "log cheaply, synthesize overnight"
/// design). This is the read corpus that synthesis consumes.
/// </para>
/// <para>
/// <see cref="ConversationId"/> and <see cref="TurnNumber"/> form the provenance link back to the
/// originating session — every episode is traceable to its source. Tenant/owner isolation is enforced
/// by the underlying graph store (<c>ComplianceAwareGraphStore</c> stamps tenant on write,
/// <c>TenantIsolatedGraphStore</c> filters on read); the episode itself carries no tenant field.
/// </para>
/// </remarks>
public sealed record WorkEpisode
{
    /// <summary>Unique identifier for this episode.</summary>
    public required Guid EpisodeId { get; init; }

    /// <summary>The agent that performed the work — provenance for which agent's behaviour to improve.</summary>
    public required string AgentId { get; init; }

    /// <summary>The conversation this turn belonged to. Provenance link + grouping key for synthesis.</summary>
    public required string ConversationId { get; init; }

    /// <summary>The 1-based turn number within the conversation.</summary>
    public required int TurnNumber { get; init; }

    /// <summary>The user's message for this turn — i.e. the task the agent was asked to perform.</summary>
    public required string UserMessage { get; init; }

    /// <summary>
    /// A (possibly truncated) summary of the assistant's response — what the agent produced.
    /// Truncated to <c>WorkMemoryConfig.ResponseSummaryMaxChars</c> at capture time to bound storage.
    /// </summary>
    public required string ResponseSummary { get; init; }

    /// <summary>Whether the turn succeeded or failed.</summary>
    public required EpisodeOutcome Outcome { get; init; }

    /// <summary>Prompt (input) tokens consumed across the LLM calls in this turn.</summary>
    public required int InputTokens { get; init; }

    /// <summary>Completion (output) tokens produced across the LLM calls in this turn.</summary>
    public required int OutputTokens { get; init; }

    /// <summary>When the episode was recorded (turn completion time).</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Total tokens consumed by the turn — the cost signal the synthesis pass trends over time.</summary>
    public int TotalTokens => InputTokens + OutputTokens;
}
