namespace Application.AI.Common.Interfaces.Orchestration.Magentic;

/// <summary>
/// One Magentic coordination round, as reported to <see cref="IMagenticProgressNotifier"/>: who the manager
/// picked to act next, what it asked, and its three judgements about the run.
/// </summary>
/// <remarks>
/// A record with required, named members rather than a parameter list: the three judgements are adjacent
/// booleans, and a call that swapped two of them would compile and report the wrong thing.
/// </remarks>
public sealed record MagenticRoundReport
{
    /// <summary>1-based round number.</summary>
    public required int Round { get; init; }

    /// <summary>The participant the manager picked, already sanitized, redacted and bounded; null when it picked none.</summary>
    public string? NextSpeaker { get; init; }

    /// <summary>What the manager asked that participant to do, already treated; null if none.</summary>
    public string? Instruction { get; init; }

    /// <summary>Whether the manager judged the request answered.</summary>
    public required bool RequestSatisfied { get; init; }

    /// <summary>Whether the manager judged the run to be repeating itself.</summary>
    public required bool InLoop { get; init; }

    /// <summary>Whether the manager judged the run to be making progress.</summary>
    public required bool Progressing { get; init; }
}
