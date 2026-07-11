using Application.AI.Common.Interfaces.Bundles;
using Application.AI.Common.Services.Bundles;
using Application.AI.Common.Services.Governance;
using Application.Core.CQRS.Agents.RunConversation;
using Domain.AI.Bundles;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Bundles;

/// <summary>
/// The shared engine that drives a single bundle run to a terminal state under its capability envelope and
/// ephemeral-agent overlay. Both triggers of a bundle run call it: the <see cref="BundleRunBackgroundService"/>
/// (async, poll-only runs) and the streaming endpoint (opt-in live runs). Concentrating the security-critical
/// ambient arming here is what stops the two triggers from diverging — see <see cref="IBundleRunExecutor"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Ordering that preserves the invariants.</strong> The lease on the staged bundle is acquired
/// <em>before</em> the run is claimed <see cref="BundleRunStatus.Running"/>: a run whose handle expired before
/// it could start is failed straight from <see cref="BundleRunStatus.Queued"/> so it never carries a bogus
/// start time, and the lease then pins the staging directory against the cleanup sweeper for the whole run so
/// the ephemeral agent can read its skills from disk. The claim itself is an atomic
/// <see cref="IBundleRunJobStore.TryBeginRun"/> compare-and-set, so if two drivers race for the same job (two
/// stream connections, or a stream and the dispatcher) exactly one wins and drives it; the loser releases its
/// lease and reports <see cref="BundleRunExecutionStatus.AlreadyClaimed"/>.
/// </para>
/// <para>
/// <strong>Ambients.</strong> The capability envelope and overlay are re-published for the duration of the
/// drive; the envelope's presence is what makes the tool-invocation governor enforce, so omitting it would
/// fail the gate open. The scope wraps the whole <see cref="RunConversationCommand"/>, which returns a fully
/// materialised result — assistant text is streamed out-of-band through the ambient turn-stream sink while the
/// command runs, so there is no deferred enumeration outliving the scope.
/// </para>
/// </remarks>
public sealed class BundleRunExecutor : IBundleRunExecutor
{
    private readonly IBundleRunJobStore _jobStore;
    private readonly IBundleHandleStore _handleStore;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<BundleRunExecutor> _logger;

    /// <summary>Initializes a new <see cref="BundleRunExecutor"/>.</summary>
    public BundleRunExecutor(
        IBundleRunJobStore jobStore,
        IBundleHandleStore handleStore,
        IServiceScopeFactory scopeFactory,
        TimeProvider time,
        ILogger<BundleRunExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(jobStore);
        ArgumentNullException.ThrowIfNull(handleStore);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _jobStore = jobStore;
        _handleStore = handleStore;
        _scopeFactory = scopeFactory;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<BundleRunExecution> ExecuteAsync(string jobId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);

        var record = _jobStore.Get(jobId);
        if (record is null)
        {
            _logger.LogWarning(
                "Bundle run {JobId} was not found when dispatched (expired or swept before pickup); dropping.",
                jobId);
            return BundleRunExecution.NotFound;
        }

        // Only a queued run is ours to drive. Anything else was already claimed by another driver or has
        // already finished — report it back without touching it.
        if (record.Status != BundleRunStatus.Queued)
            return BundleRunExecution.AlreadyClaimed(record);

        // Acquire the handle BEFORE claiming Running: a run whose handle expired before pickup is failed
        // straight from Queued (never stamped with a start time). While the run holds the lease the sweeper
        // cannot delete the staging directory out from under the ephemeral agent.
        using var lease = _handleStore.Acquire(record.Handle);
        if (lease is null)
        {
            _logger.LogWarning(
                "Bundle run {JobId} could not start: handle {Handle} expired before the run began.",
                jobId, record.Handle);
            var failed = record with
            {
                Status = BundleRunStatus.Failed,
                Error = "The bundle handle expired before the run started.",
                CompletedAt = _time.GetUtcNow()
            };
            _jobStore.Update(failed);
            return BundleRunExecution.Ran(failed);
        }

        // Atomically claim the run. If another driver won the race, stand down and release the lease. The
        // carried snapshot is the one already in hand — no caller reads it, so a fresh store read would be
        // wasted work on this rare race-loss path.
        var running = _jobStore.TryBeginRun(jobId, _time.GetUtcNow());
        if (running is null)
            return BundleRunExecution.AlreadyClaimed(record);

        return await DriveAsync(running, lease, cancellationToken).ConfigureAwait(false);
    }

    private async Task<BundleRunExecution> DriveAsync(
        BundleRunRecord running, IBundleHandleLease lease, CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunConversationAsync(running, lease.Bundle, cancellationToken).ConfigureAwait(false);

            var succeeded = running with
            {
                Status = BundleRunStatus.Succeeded,
                Outcome = MapOutcome(result),
                CompletedAt = _time.GetUtcNow()
            };
            _jobStore.Update(succeeded);
            return BundleRunExecution.Ran(succeeded);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown or client disconnect — record the stop and propagate so a caller draining a queue
            // exits its loop. The streaming caller's connection is already gone.
            _jobStore.Update(running with
            {
                Status = BundleRunStatus.Failed,
                Error = "The run was cancelled.",
                CompletedAt = _time.GetUtcNow()
            });
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bundle run {JobId} failed with an unhandled exception.", running.JobId);
            var failed = running with
            {
                Status = BundleRunStatus.Failed,
                Error = "bundle_run.unhandled_exception",
                CompletedAt = _time.GetUtcNow()
            };
            _jobStore.Update(failed);
            return BundleRunExecution.Ran(failed);
        }
    }

    private async Task<ConversationResult> RunConversationAsync(
        BundleRunRecord record,
        StagedBundle staged,
        CancellationToken cancellationToken)
    {
        var overlay = new EphemeralAgentOverlay
        {
            Agent = staged.Agent,
            OwnedSkills = staged.OwnedSkills
        };

        await using var scope = _scopeFactory.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        // Arm BOTH ambients for the whole conversation: the overlay so the ephemeral agent + its owned skills
        // resolve, and the envelope so the governor enforces the per-caller grant. Disposed in reverse when the
        // (materialised) conversation returns.
        using (EphemeralAgentOverlayAccessor.Begin(overlay))
        using (CapabilityEnvelopeAccessor.Begin(record.Envelope))
        {
            var command = new RunConversationCommand
            {
                AgentName = record.AgentName,
                UserMessages = record.UserMessages,
                MaxTurns = record.MaxTurns,
                ConversationId = record.JobId
            };

            return await mediator.Send(command, cancellationToken).ConfigureAwait(false);
        }
    }

    private static BundleRunOutcome MapOutcome(ConversationResult result) => new()
    {
        ConversationSucceeded = result.Success,
        FinalResponse = result.FinalResponse,
        TurnCount = result.Turns.Count,
        TotalToolInvocations = result.TotalToolInvocations,
        BudgetExhausted = result.BudgetExhausted,
        ConversationError = result.Error
    };
}
