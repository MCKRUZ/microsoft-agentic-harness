namespace Domain.Common.Compliance;

/// <summary>
/// The ids a person's planned-task runs recorded data under, which no conversation of theirs leads to (#906).
/// </summary>
/// <remarks>
/// <para>
/// A planned task's model steps each run as their own short conversation, and its tool steps keep their large results
/// under the run's own id. Neither id appears among the person's conversations, so the stores that hold only an id
/// (observability sessions, spilled tool results, prompt-usage records, token budgets, the call-once ledger) are
/// reachable only through the runs the plan store recorded. They are read before anything is deleted, like the
/// conversation ids.
/// </para>
/// </remarks>
public sealed record PlanRunTraces
{
    /// <summary>No planned-task runs.</summary>
    public static PlanRunTraces Empty { get; } = new([], [], []);

    /// <summary>Initializes a new instance of the <see cref="PlanRunTraces"/> class.</summary>
    /// <param name="stepConversationIds">The conversation id of every model step the runs could have executed.</param>
    /// <param name="toolResultScopeIds">The id each run kept its tool steps' spilled results under.</param>
    /// <param name="runBudgetKeys">The token-budget key of each run.</param>
    public PlanRunTraces(
        IReadOnlyList<string> stepConversationIds,
        IReadOnlyList<string> toolResultScopeIds,
        IReadOnlyList<string> runBudgetKeys)
    {
        ArgumentNullException.ThrowIfNull(stepConversationIds);
        ArgumentNullException.ThrowIfNull(toolResultScopeIds);
        ArgumentNullException.ThrowIfNull(runBudgetKeys);

        StepConversationIds = stepConversationIds;
        ToolResultScopeIds = toolResultScopeIds;
        RunBudgetKeys = runBudgetKeys;
    }

    /// <summary>
    /// The conversation id of every model step the person's runs could have executed. Each is a conversation in its own
    /// right in the stores keyed by conversation id, so <see cref="ErasureSubject.AllConversationIds"/> includes them.
    /// </summary>
    public IReadOnlyList<string> StepConversationIds { get; }

    /// <summary>The id each run kept its tool steps' spilled results under; only the tool-result store uses it.</summary>
    public IReadOnlyList<string> ToolResultScopeIds { get; }

    /// <summary>The token-budget key of each run; only the budget trackers use it.</summary>
    public IReadOnlyList<string> RunBudgetKeys { get; }

    /// <summary>
    /// The plan store's own tokens for the run records it read, opaque to everyone else. It erases only those, as they
    /// were when read: a run recorded or changed since, or still in progress, keeps its plan, and the receipt asks for
    /// erasure to run again. <see langword="null"/> when not read from a plan store, in which case every run is erased.
    /// </summary>
    public IReadOnlyList<string>? ListedRuns { get; init; }

    /// <summary>Whether no id was read at all.</summary>
    public bool IsEmpty => StepConversationIds.Count == 0 && ToolResultScopeIds.Count == 0 && RunBudgetKeys.Count == 0;

    /// <summary>These ids without any that are also in <paramref name="other"/>.</summary>
    /// <param name="other">The ids to leave out.</param>
    /// <returns>The ids only these traces hold.</returns>
    public PlanRunTraces Except(PlanRunTraces other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new PlanRunTraces(
            [.. StepConversationIds.Except(other.StepConversationIds, StringComparer.Ordinal)],
            [.. ToolResultScopeIds.Except(other.ToolResultScopeIds, StringComparer.Ordinal)],
            [.. RunBudgetKeys.Except(other.RunBudgetKeys, StringComparer.Ordinal)]) { ListedRuns = ListedRuns };
    }
}
