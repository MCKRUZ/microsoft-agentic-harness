using Application.AI.Common.Interfaces.Orchestration.Magentic;

namespace Infrastructure.AI.Tests.Orchestration.Magentic;

/// <summary>
/// An <see cref="IMagenticProgressNotifier"/> that records every call, in order, and can be told to
/// throw — so a test can assert both what a run reported and that a failing notifier cannot take the
/// run down with it.
/// </summary>
internal sealed class RecordingMagenticProgressNotifier : IMagenticProgressNotifier
{
    private readonly List<string> _order = [];

    /// <summary>When set, every call throws this exception after recording itself.</summary>
    public Exception? ThrowOnEveryCall { get; init; }

    /// <summary>When true, a terminal call (completed / failed) never returns until its token is cancelled.</summary>
    public bool BlockTerminalCallsUntilCancelled { get; init; }

    /// <summary>The kind of each call, in the order received.</summary>
    public IReadOnlyList<string> Order => _order;

    public List<(Guid WorkflowId, string Name, IReadOnlyList<string> Participants)> Started { get; } = [];

    public List<(int Version, string Text)> Plans { get; } = [];

    public List<MagenticRoundReport> Rounds { get; } = [];

    public List<(string Text, bool Truncated, bool IsStalled)> Reviews { get; } = [];

    public List<(string Reason, int Rounds)> Completed { get; } = [];

    public List<string> Failed { get; } = [];

    /// <summary>The cancellation token each call was made with, to check terminal calls survive a cancelled run.</summary>
    public List<CancellationToken> Tokens { get; } = [];

    public Task NotifyWorkflowStartedAsync(
        Guid workflowId, string workflowName, IReadOnlyList<string> participants, CancellationToken ct)
        => Record("started", () => Started.Add((workflowId, workflowName, participants)), ct);

    public Task NotifyPlanAsync(Guid workflowId, int planVersion, string planText, CancellationToken ct)
        => Record("plan", () => Plans.Add((planVersion, planText)), ct);

    public Task NotifyRoundAsync(Guid workflowId, MagenticRoundReport round, CancellationToken ct)
        => Record("round", () => Rounds.Add(round), ct);

    public Task NotifyPlanReviewRequestedAsync(
        Guid workflowId, string planText, bool planTruncated, bool isStalled, CancellationToken ct)
        => Record("review", () => Reviews.Add((planText, planTruncated, isStalled)), ct);

    public Task NotifyWorkflowCompletedAsync(
        Guid workflowId, string completionReason, int roundsExecuted, CancellationToken ct)
        => Terminal("completed", () => Completed.Add((completionReason, roundsExecuted)), ct);

    public Task NotifyWorkflowFailedAsync(Guid workflowId, string errorCode, CancellationToken ct)
        => Terminal("failed", () => Failed.Add(errorCode), ct);

    private async Task Terminal(string kind, Action store, CancellationToken ct)
    {
        await Record(kind, store, ct);
        if (BlockTerminalCallsUntilCancelled)
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    private Task Record(string kind, Action store, CancellationToken ct)
    {
        _order.Add(kind);
        Tokens.Add(ct);
        store();
        return ThrowOnEveryCall is null ? Task.CompletedTask : Task.FromException(ThrowOnEveryCall);
    }
}
