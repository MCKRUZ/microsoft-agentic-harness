using Domain.AI.Bundles;

namespace Presentation.BundleApi.DTOs;

/// <summary>Response to a successful bundle registration.</summary>
public sealed record RegisterBundleResponse
{
    /// <summary>The opaque handle used to run or delete the staged bundle.</summary>
    public required string Handle { get; init; }

    /// <summary>The earliest instant the handle expires if it is not used again before then.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>Request body for starting a bundle run.</summary>
public sealed record RunBundleRequest
{
    /// <summary>The user messages seeding the conversation. One turn per message, bounded by <see cref="MaxTurns"/>.</summary>
    public IReadOnlyList<string> UserMessages { get; init; } = [];

    /// <summary>The maximum number of turns the conversation may run. Defaults to 10.</summary>
    public int MaxTurns { get; init; } = 10;
}

/// <summary>Response to a successfully queued bundle run.</summary>
public sealed record StartRunResponse
{
    /// <summary>The opaque id of the queued run job.</summary>
    public required string JobId { get; init; }

    /// <summary>Relative URL to poll for the run's status and result.</summary>
    public required string StatusUrl { get; init; }
}

/// <summary>
/// The pollable view of a bundle run. Deliberately a projection of the internal <see cref="BundleRunRecord"/>:
/// it never surfaces the capability envelope, the seed messages, or any other execution input — only the
/// run's lifecycle state and, once complete, its outcome.
/// </summary>
public sealed record BundleRunResponse
{
    /// <summary>The run's job id.</summary>
    public required string JobId { get; init; }

    /// <summary>The current lifecycle state (Queued, Running, Succeeded, Failed).</summary>
    public required string Status { get; init; }

    /// <summary>A caller-safe reason when the run failed outright; null otherwise.</summary>
    public string? Error { get; init; }

    /// <summary>When the run was created and queued.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When execution began; null while still queued.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>When the run reached a terminal state; null before then.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>The run outcome once it has succeeded; null before then.</summary>
    public BundleRunOutcomeResponse? Result { get; init; }

    /// <summary>Projects an internal run record onto the pollable response, dropping all execution inputs.</summary>
    public static BundleRunResponse FromRecord(BundleRunRecord record) => new()
    {
        JobId = record.JobId,
        Status = record.Status.ToString(),
        Error = record.Error,
        CreatedAt = record.CreatedAt,
        StartedAt = record.StartedAt,
        CompletedAt = record.CompletedAt,
        Result = record.Outcome is null ? null : BundleRunOutcomeResponse.FromOutcome(record.Outcome)
    };
}

/// <summary>The terminal outcome of a bundle run, as surfaced to a poller.</summary>
public sealed record BundleRunOutcomeResponse
{
    /// <summary>Whether the conversation itself reported success (distinct from the run completing).</summary>
    public required bool ConversationSucceeded { get; init; }

    /// <summary>The final agent response, or empty when none was produced.</summary>
    public required string FinalResponse { get; init; }

    /// <summary>The number of turns that executed.</summary>
    public required int TurnCount { get; init; }

    /// <summary>The total number of tool invocations across all turns.</summary>
    public required int TotalToolInvocations { get; init; }

    /// <summary>Whether the conversation stopped early on its lifetime token budget.</summary>
    public bool BudgetExhausted { get; init; }

    /// <summary>The conversation-level error when <see cref="ConversationSucceeded"/> is false; null otherwise.</summary>
    public string? ConversationError { get; init; }

    /// <summary>Projects an internal outcome onto the response shape.</summary>
    public static BundleRunOutcomeResponse FromOutcome(BundleRunOutcome outcome) => new()
    {
        ConversationSucceeded = outcome.ConversationSucceeded,
        FinalResponse = outcome.FinalResponse,
        TurnCount = outcome.TurnCount,
        TotalToolInvocations = outcome.TotalToolInvocations,
        BudgetExhausted = outcome.BudgetExhausted,
        ConversationError = outcome.ConversationError
    };
}
