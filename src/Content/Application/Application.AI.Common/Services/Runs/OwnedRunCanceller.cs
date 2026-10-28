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
/// ended, and an executing workflow is signalled. Only the words differ: the watchers are told the run was stopped by
/// erasure, and the withdrawals are attributed to <see cref="WithdrawnBy"/>, a system actor. Naming the person there would
/// write their id into the escalation audit, which is kept on purpose, in the act of erasing them.
/// </para>
/// <para>
/// <strong>Authorised by selection.</strong> The cancel commands authorise a run by reading it through the caller's
/// scope; erasure has no caller to scope by, and its subject may be erased by an operator. The runs here are exactly the
/// ones <see cref="IRunJobStore.GetLiveRunsOwnedBy"/> lists for the person, so nobody else's run is reached, and the
/// person's own are reached whoever asked.
/// </para>
/// <para>
/// <strong>Cancelled again on every poll.</strong> An executing workflow can park on a gate before it reaches the point
/// that notices its signal, and a parked run can be resumed onto the queue by an approval that arrives meanwhile. Either
/// would slip past a single pass, so each poll repeats the cancel for the runs still live; the store's transitions are
/// atomic, so repeating one is harmless.
/// </para>
/// <para>
/// <strong>Bounded.</strong> An executing evaluation cannot be signalled and a workflow stops only at its next checkpoint,
/// so the wait gives up after <see cref="DefaultWait"/>, as the planned-task canceller does. A run started during the wait
/// is not tracked, so it cannot stretch it out; it is kept, and the receipt says to run erasure again.
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
    /// <param name="logger">Logger for how many runs were stopped.</param>
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
    public async Task<int> CancelRunsAsync(string ownerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

        var live = _store.GetLiveRunsOwnedBy(ownerId);
        if (live.Count == 0)
            return 0;

        var tracked = live.Select(r => r.JobId).ToHashSet(StringComparer.Ordinal);
        var deadline = _time.GetUtcNow() + _wait;
        var stopped = 0;

        while (true)
        {
            foreach (var run in live)
                stopped += (await CancelAsync(run)).StoppedImmediately ? 1 : 0;

            live = StillLive(ownerId, tracked);
            if (live.Count == 0 || _time.GetUtcNow() >= deadline)
                break;

            await Task.Delay(PollEvery, _time, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Erasure cancelled {Stopped} of {Tracked} live run(s); {Remaining} had not ended when it went on.",
            stopped, tracked.Count, live.Count);

        return live.Count;
    }

    private Task<RunCancellationOutcome> CancelAsync(RunRecord run) =>
        RunCancellation.CancelAsync(
            _store,
            _progress,
            _escalations,
            _registry,
            _logger,
            run,
            new RunCancellationNotice(_time.GetUtcNow(), Detail, WithdrawalReason, WithdrawnBy));

    private List<RunRecord> StillLive(string ownerId, HashSet<string> tracked) =>
        [.. _store.GetLiveRunsOwnedBy(ownerId).Where(r => tracked.Contains(r.JobId))];
}
