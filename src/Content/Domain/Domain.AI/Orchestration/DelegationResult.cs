using Domain.AI.Governance;

namespace Domain.AI.Orchestration;

/// <summary>
/// Outcome of a delegation, returned by the supervisor after a delegated agent completes.
/// Use static factory methods for construction.
/// </summary>
public sealed record DelegationResult
{
    /// <summary>Whether the delegation completed successfully.</summary>
    public required bool IsSuccess { get; init; }

    /// <summary>Output from the delegated agent. Null on failure.</summary>
    public string? Output { get; init; }

    /// <summary>Reason for failure. Null on success.</summary>
    public string? FailureReason { get; init; }

    /// <summary>Populated when the failure was due to an autonomy tier violation.</summary>
    public AutonomyExceededResult? AutonomyExceeded { get; init; }

    /// <summary>Number of tokens consumed by the delegated agent.</summary>
    public int TokensUsed { get; init; }

    /// <summary>Execution duration in milliseconds.</summary>
    public long DurationMs { get; init; }

    /// <summary>Creates a successful delegation result.</summary>
    public static DelegationResult Success(string output, int tokensUsed, long durationMs) => new()
    {
        IsSuccess = true,
        Output = output,
        TokensUsed = tokensUsed,
        DurationMs = durationMs
    };

    /// <summary>Creates a failed delegation result.</summary>
    public static DelegationResult Fail(string reason) => new()
    {
        IsSuccess = false,
        FailureReason = reason
    };

    /// <summary>Creates a failed result due to an autonomy tier violation.</summary>
    public static DelegationResult FailAutonomyExceeded(AutonomyExceededResult exceeded) => new()
    {
        IsSuccess = false,
        FailureReason = $"Autonomy tier violation: {exceeded.Reason}",
        AutonomyExceeded = exceeded
    };
}
