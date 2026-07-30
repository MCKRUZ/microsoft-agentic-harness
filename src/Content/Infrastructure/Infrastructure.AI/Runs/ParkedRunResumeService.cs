using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Runs;
using Domain.AI.Runs;
using Domain.Common.Config;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Runs;

/// <summary>
/// Returns parked runs to the queue once a decision they were waiting on has been answered.
/// </summary>
/// <remarks>
/// <para>
/// This is the trigger that makes a human gate more than a place work stops. Everything either side of
/// it already existed: a gate queues an escalation and parks its step, and the plan executor reconciles
/// blocked steps against their verdicts on its next execution. Without something to notice the verdict
/// and ask for that next execution, an approved gate simply never released.
/// </para>
/// <para>
/// <strong>It re-reads verdicts on a clock rather than reacting to each decision.</strong> An event
/// would be lower-latency and is the obvious design, but it is lost in exactly the cases that matter:
/// a decision taken while the host is down, or on another instance, would leave the run parked until
/// the parked-run ceiling failed it — turning an approval into a silent expiry days later. Re-reading
/// is the only form of this trigger that survives a restart, and the latency it costs is measured
/// against a wait that was already however long a human took.
/// </para>
/// <para>
/// <strong>A rejected decision resumes the run too.</strong> Resuming is not "continue the work" — it
/// is "let the plan act on the answer". A denial has to re-enter execution for the gate to fail and
/// the run to reach a terminal state; treating only approvals as worth resuming would leave every
/// rejected workflow parked.
/// </para>
/// <para>
/// <strong>The escalation service is read, never written.</strong> Resuming asks a question of the
/// governance subsystem and acts within the run substrate. Nothing here can alter a verdict, and the
/// escalation subsystem knows nothing about runs — which keeps a decision recorded for compliance
/// independent of whether any run happened to be waiting on it.
/// </para>
/// </remarks>
internal sealed class ParkedRunResumeService : BackgroundService
{
    /// <summary>
    /// Floor on the check interval. Configuration is validated as positive, but a positive value can
    /// still be small enough to turn this into a spin loop; the floor makes a mis-set value slow
    /// rather than harmful.
    /// </summary>
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    private readonly IRunJobStore _store;
    private readonly IRunDispatchQueue _queue;
    private readonly IEscalationService _escalations;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<ParkedRunResumeService> _logger;

    /// <summary>Initializes a new <see cref="ParkedRunResumeService"/>.</summary>
    public ParkedRunResumeService(
        IRunJobStore store,
        IRunDispatchQueue queue,
        IEscalationService escalations,
        IOptionsMonitor<AppConfig> config,
        ILogger<ParkedRunResumeService> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(escalations);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _queue = queue;
        _escalations = escalations;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var interval = _config.CurrentValue.AI.WorkflowSubmission.ParkedRunResumeInterval;
                if (interval < MinInterval)
                    interval = MinInterval;

                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
                await ResumeAnsweredRunsAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting down — expected.
        }
    }

    private async Task ResumeAnsweredRunsAsync(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var parked in _store.GetParkedRuns())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (await HasAnsweredDecisionAsync(parked, cancellationToken).ConfigureAwait(false))
                    await ResumeAsync(parked, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A failed pass must not take the service down: the next tick would never come, and every
            // parked run in the host would then wait out the ceiling regardless of its verdict.
            _logger.LogError(ex, "Parked-run resume check failed; will retry on the next interval.");
        }
    }

    /// <summary>
    /// Whether any decision this run parked on has reached a verdict.
    /// </summary>
    /// <remarks>
    /// Stops at the first answer. A plan parked on two gates needs one verdict to be worth another
    /// execution — reconciliation re-reads every blocked step anyway, and if the second gate is still
    /// open the plan simply parks again on that one alone.
    /// </remarks>
    private async Task<bool> HasAnsweredDecisionAsync(RunRecord parked, CancellationToken cancellationToken)
    {
        foreach (var escalationId in parked.AwaitingEscalationIds)
        {
            var outcome = await _escalations.GetOutcomeAsync(escalationId, cancellationToken).ConfigureAwait(false);
            if (outcome is not null)
                return true;
        }

        return false;
    }

    private async Task ResumeAsync(RunRecord parked, CancellationToken cancellationToken)
    {
        // Only the caller that wins the transition enqueues. Deciding from the record read above would
        // let two passes — or a pass racing the ceiling that is about to fail this very run — both act
        // on the same park.
        var resumed = _store.TryResume(parked.JobId);
        if (resumed is null)
            return;

        try
        {
            await _queue.EnqueueAsync(resumed.JobId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The run is Queued at this point but nothing holds it: no dispatcher will ever claim a run
            // that is not in the queue, and the parked-run ceiling only ever looks at parked runs — so
            // leaving it here strands the run in the one state from which nothing can recover it.
            // Restoring the record it had a moment ago puts it back under the ceiling and lets the next
            // pass try again.
            _store.Update(parked);
            throw;
        }

        _logger.LogInformation(
            "Run {JobId} resumed: a decision it was waiting on has been answered.", resumed.JobId);
    }
}
