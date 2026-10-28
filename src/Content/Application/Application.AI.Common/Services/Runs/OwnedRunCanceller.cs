using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.Compliance;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Interfaces.Runs;
using Domain.AI.Runs;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Runs;

/// <summary>
/// <see cref="IOwnedRunCanceller"/> over the run store and the shared cancel path, <see cref="RunCancellation"/> (#992).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The same path an owner's cancel takes.</strong> Each run goes through <see cref="RunCancellation"/>, so a queued
/// run is cancelled and never claimed, a parked one is cancelled and its approvals withdrawn, its watchers are told it
/// ended, and an executing workflow is signalled through its plan, which reaches only the person's own runs (see
/// <see cref="RunCancellation"/>). Only the words differ: the watchers are told the run was stopped by erasure, and the
/// withdrawals are attributed to <see cref="WithdrawnBy"/>, a system actor. Naming the person there would write their id
/// into the escalation audit, which is kept on purpose, in the act of erasing them.
/// </para>
/// <para>
/// <strong>Authorised by selection.</strong> The cancel commands authorise a run by reading it through the caller's
/// scope; erasure has no caller to scope by, and its subject may be erased by an operator. The runs here are exactly the
/// ones <see cref="IRunJobStore.GetLiveRunsOwnedBy"/> lists for the person, so nobody else's run is reached, and the
/// person's own are reached whoever asked.
/// </para>
/// <para>
/// <strong>Repeated on every poll, for every run of the person's.</strong> An executing workflow can park on a gate before
/// it notices its signal, a parked run can be resumed onto the queue by an approval that arrives meanwhile, and a schedule
/// or a client can queue a new run, which an idle dispatcher claims at once. Each poll therefore cancels whatever of the
/// person's is queued or parked at that moment and signals whatever is executing; the store's transitions are atomic and
/// a repeated signal is a no-op, so repeating either is harmless. One run that cannot be stopped is logged and skipped,
/// so it does not leave the rest running.
/// </para>
/// <para>
/// <strong>Bounded, and only on what was executing when the wait began.</strong> An executing evaluation cannot be
/// signalled and a workflow stops only at its next checkpoint, so the wait gives up after <see cref="DefaultWait"/>, as
/// the planned-task canceller does, and alongside it, so the two do not add up. A run that starts executing during the
/// wait is signalled but cannot stretch it out; if it has not ended it is kept, and the receipt says to run erasure again.
/// </para>
/// </remarks>
public sealed class OwnedRunCanceller : IOwnedRunCanceller
{
    /// <summary>How long erasure waits for the person's executing runs to end before going on without them.</summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(30);

    /// <summary>Who erasure's approval withdrawals are attributed to in the escalation audit.</summary>
    public const string WithdrawnBy = "system:personal-data-erasure";

    private const string Detail = "The run was cancelled because its owner's personal data was erased.";
    private const string WithdrawalReason =
        "The run this approval was raised for was cancelled because its owner's personal data was erased.";

    private static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(250);

    private readonly IRunJobStore _store;
    private readonly IRunProgressBroker _progress;
    private readonly IEscalationService _escalations;
    private readonly IPlanRunCancellationRegistry _registry;
    private readonly TimeProvider _time;
    private readonly ILogger<OwnedRunCanceller> _logger;
    private readonly TimeSpan _wait;

    /// <summary>Initializes a new instance of the <see cref="OwnedRunCanceller"/> class.</summary>
    /// <param name="store">Lists and cancels the person's runs.</param>
    /// <param name="progress">Tells a cancelled run's watchers it ended.</param>
    /// <param name="escalations">Withdraws the approvals a parked run was waiting on.</param>
    /// <param name="registry">Signals an executing workflow to stop.</param>
    /// <param name="time">Stamps the cancellations and times the wait.</param>
    /// <param name="logger">Logger for how many runs were stopped, and for one that could not be.</param>
    /// <param name="wait">How long to wait for executing runs to end; <see cref="DefaultWait"/> when omitted.</param>
    public OwnedRunCanceller(
        IRunJobStore store,
        IRunProgressBroker progress,
        IEscalationService escalations,
        IPlanRunCancellationRegistry registry,
        TimeProvider time,
        ILogger<OwnedRunCanceller> logger,
        TimeSpan? wait = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(escalations);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _progress = progress;
        _escalations = escalations;
        _registry = registry;
        _time = time;
        _logger = logger;
        _wait = wait ?? DefaultWait;
    }

    /// <inheritdoc />
    public async Task<int> CancelRunsAsync(string ownerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

        var cancelled = await StopAsync(_store.GetLiveRunsOwnedBy(ownerId)).ConfigureAwait(false);
        if (cancelled > 0)
            _logger.LogInformation("Erasure cancelled {Count} queued or parked run(s).", cancelled);

        return cancelled;
    }

    /// <inheritdoc />
    public async Task<int> WaitForRunsAsync(string ownerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

        var live = _store.GetLiveRunsOwnedBy(ownerId);
        var executing = live.Where(r => r.Status == RunStatus.Running).Select(r => r.JobId).ToHashSet(StringComparer.Ordinal);
        var deadline = _time.GetUtcNow() + _wait;

        while (true)
        {
            await StopAsync(live).ConfigureAwait(false);
            live = _store.GetLiveRunsOwnedBy(ownerId);
            executing.IntersectWith(live.Select(r => r.JobId));
            if (executing.Count == 0 || _time.GetUtcNow() >= deadline)
                break;

            await Task.Delay(PollEvery, _time, cancellationToken).ConfigureAwait(false);
            live = _store.GetLiveRunsOwnedBy(ownerId);
        }

        if (live.Count > 0)
            _logger.LogInformation("Erasure stopped waiting with {Count} run(s) not yet ended.", live.Count);

        return live.Count;
    }

    /// <summary>
    /// Cancels each queued or parked run in <paramref name="runs"/> and signals each executing workflow, returning how many
    /// were cancelled.
    /// </summary>
    private async Task<int> StopAsync(IReadOnlyList<RunRecord> runs)
    {
        var cancelled = 0;
        foreach (var run in runs)
        {
            try
            {
                var outcome = await RunCancellation.CancelAsync(
                    _store,
                    _progress,
                    _escalations,
                    _registry,
                    _logger,
                    run,
                    new RunCancellationNotice(_time.GetUtcNow(), Detail, WithdrawalReason, WithdrawnBy)).ConfigureAwait(false);
                cancelled += outcome.StoppedImmediately ? 1 : 0;
            }
            catch (Exception ex)
            {
                // The rest of the person's runs must still be stopped. The store's transition or the signal is what failed,
                // so this one is still live and counted as kept.
                _logger.LogError(MessageWithheldException.From(ex), "Erasure could not stop run {JobId}.", run.JobId);
            }
        }

        return cancelled;
    }
}
