namespace Domain.AI.Bundles;

/// <summary>
/// The terminal outcome of a bundle run, projected from the conversation the ephemeral agent ran. This is
/// a Domain-pure summary — the pieces a poller needs — deliberately not the Application-layer
/// <c>ConversationResult</c> itself: the bundle subsystem's contracts live in <c>Domain.AI</c> /
/// <c>Application.AI.Common</c>, which cannot see <c>Application.Core</c> where that result type lives. The
/// background service maps the rich result onto this value when it writes the terminal record.
/// </summary>
/// <remarks>
/// A run reaching <see cref="BundleRunStatus.Succeeded"/> means the conversation completed; it does not mean
/// every turn produced a good answer. <see cref="ConversationSucceeded"/> carries that inner distinction —
/// a conversation whose agent reported a failed turn completed (so the run is Succeeded) but reports
/// <see cref="ConversationSucceeded"/> false with the reason in <see cref="ConversationError"/>. Only an
/// unhandled exception or a missing staged bundle makes the run itself <see cref="BundleRunStatus.Failed"/>.
/// </remarks>
public sealed record BundleRunOutcome
{
    /// <summary>
    /// Whether the conversation itself reported success. False when a turn failed or the agent errored,
    /// even though the run mechanically completed (and is therefore <see cref="BundleRunStatus.Succeeded"/>).
    /// </summary>
    public required bool ConversationSucceeded { get; init; }

    /// <summary>The final agent response of the conversation, or empty when no turn produced one.</summary>
    public required string FinalResponse { get; init; }

    /// <summary>The number of turns that executed before the conversation ended.</summary>
    public required int TurnCount { get; init; }

    /// <summary>The total number of tool invocations across all turns.</summary>
    public required int TotalToolInvocations { get; init; }

    /// <summary>
    /// Whether the conversation stopped early because it exhausted its lifetime token budget. A graceful
    /// stop, not a failure — the turns that ran are still reflected in <see cref="TurnCount"/>.
    /// </summary>
    public bool BudgetExhausted { get; init; }

    /// <summary>
    /// The conversation-level error when <see cref="ConversationSucceeded"/> is false (e.g. "Turn 2
    /// failed: …"), otherwise null. Distinct from the run-level <c>Error</c> on the record, which is set
    /// only when the run failed outright.
    /// </summary>
    public string? ConversationError { get; init; }
}
