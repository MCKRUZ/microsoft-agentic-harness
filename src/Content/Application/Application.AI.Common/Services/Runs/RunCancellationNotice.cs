namespace Application.AI.Common.Services.Runs;

/// <summary>
/// What a cancellation records, and in whose name: the words a run's watchers and its approvers see, which differ between
/// an owner stopping their own run and erasure stopping it (#992).
/// </summary>
/// <param name="CancelledAt">The completion time recorded on a run stopped before it executed.</param>
/// <param name="Detail">Caller-safe detail on the terminal progress event that ends a watcher's stream.</param>
/// <param name="WithdrawalReason">Caller-safe reason recorded on each approval the run was parked on.</param>
/// <param name="WithdrawnBy">
/// Who the withdrawals are attributed to in the escalation audit. An approver-shaped identity where one exists; a
/// system actor when no person asked, so the audit does not name someone untruthfully.
/// </param>
public sealed record RunCancellationNotice(
    DateTimeOffset CancelledAt,
    string Detail,
    string WithdrawalReason,
    string WithdrawnBy);
