using Application.AI.Common.Interfaces.Bundles;
using Application.AI.Common.Services.Bundles;
using Application.AI.Common.Services.Governance;
using Application.Core.CQRS.Agents.RunConversation;
using Domain.AI.Bundles;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Bundles;

/// <summary>
/// Drains the <see cref="IBundleRunDispatchQueue"/> and executes each queued bundle run: it acquires the
/// staged bundle behind the run's handle, re-publishes the run's ephemeral-agent overlay and capability
/// envelope ambiently, drives a full <see cref="RunConversationCommand"/>, and records the terminal outcome
/// on the run record. Mirrors <c>ChangeProposalBackgroundService</c>: one DI scope per run, failure-isolated
/// so one bad run never stalls the queue.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the ambients are re-armed here.</strong> The dispatcher runs on a thread-pool flow detached
/// from the HTTP request that enqueued the run, so the ambient overlay and capability envelope set during
/// that request do not flow to it. Both are re-published from the run record and the staged bundle for the
/// duration of the run. Arming the envelope is what makes the tool-invocation governor enforce — the
/// governor forces enforcement on precisely from the envelope's presence, and the standard turn handler
/// arms <c>ToolGovernanceAccessor</c> per turn — so without re-arming the envelope here the governor would
/// see none and fail <em>open</em>.
/// </para>
/// <para>
/// The envelope scope stays open across the whole <see cref="RunConversationCommand"/>, which returns a
/// fully-materialised result (not a deferred stream), so disposing the scope once <c>Send</c> returns is
/// safe. The streaming run path (a later phase) must instead keep the scope open across the full stream
/// enumeration — see the deferred-execution note on <c>CapabilityEnvelopeAccessor</c>.
/// </para>
/// <para>
/// The staged bundle is <em>pinned</em> through an <see cref="IBundleHandleLease"/> for the whole run, so
/// the cleanup sweeper cannot delete its staging directory while the ephemeral agent is reading its skills
/// from disk. A run whose handle expired before pickup fails cleanly with a caller-safe reason.
/// </para>
/// </remarks>
public sealed class BundleRunBackgroundService : BackgroundService
{
    private readonly IBundleRunDispatchQueue _queue;
    private readonly IBundleRunJobStore _jobStore;
    private readonly IBundleHandleStore _handleStore;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<BundleRunBackgroundService> _logger;

    /// <summary>Initializes a new <see cref="BundleRunBackgroundService"/>.</summary>
    public BundleRunBackgroundService(
        IBundleRunDispatchQueue queue,
        IBundleRunJobStore jobStore,
        IBundleHandleStore handleStore,
        IServiceScopeFactory scopeFactory,
        TimeProvider time,
        ILogger<BundleRunBackgroundService> logger)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(jobStore);
        ArgumentNullException.ThrowIfNull(handleStore);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _queue = queue;
        _jobStore = jobStore;
        _handleStore = handleStore;
        _scopeFactory = scopeFactory;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var jobId in _queue.DequeueAllAsync(stoppingToken).ConfigureAwait(false))
        {
            if (stoppingToken.IsCancellationRequested)
                break;

            await DispatchOneAsync(jobId, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task DispatchOneAsync(string jobId, CancellationToken stoppingToken)
    {
        var record = _jobStore.Get(jobId);
        if (record is null)
        {
            _logger.LogWarning(
                "Bundle run {JobId} was not found when dispatched (expired or swept before pickup); dropping.",
                jobId);
            return;
        }

        // Acquire the handle BEFORE transitioning to Running: a run that never starts (its handle expired
        // before pickup) is marked Failed straight from Queued, so it never carries a bogus StartedAt.
        using var lease = _handleStore.Acquire(record.Handle);
        if (lease is null)
        {
            _logger.LogWarning(
                "Bundle run {JobId} could not start: handle {Handle} expired before the run began.",
                jobId, record.Handle);
            _jobStore.Update(record with
            {
                Status = BundleRunStatus.Failed,
                Error = "The bundle handle expired before the run started.",
                CompletedAt = _time.GetUtcNow()
            });
            return;
        }

        record = record with { Status = BundleRunStatus.Running, StartedAt = _time.GetUtcNow() };
        _jobStore.Update(record);

        try
        {
            var result = await RunConversationAsync(record, lease.Bundle, stoppingToken).ConfigureAwait(false);

            _jobStore.Update(record with
            {
                Status = BundleRunStatus.Succeeded,
                Outcome = MapOutcome(result),
                CompletedAt = _time.GetUtcNow()
            });
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _jobStore.Update(record with
            {
                Status = BundleRunStatus.Failed,
                Error = "The run was cancelled by host shutdown.",
                CompletedAt = _time.GetUtcNow()
            });
            throw; // propagate to exit the drain loop on shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bundle run {JobId} failed with an unhandled exception.", jobId);
            _jobStore.Update(record with
            {
                Status = BundleRunStatus.Failed,
                Error = "bundle_run.unhandled_exception",
                CompletedAt = _time.GetUtcNow()
            });
        }
    }

    private async Task<ConversationResult> RunConversationAsync(
        BundleRunRecord record,
        StagedBundle staged,
        CancellationToken stoppingToken)
    {
        var overlay = new EphemeralAgentOverlay
        {
            Agent = staged.Agent,
            OwnedSkills = staged.OwnedSkills
        };

        await using var scope = _scopeFactory.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        // Arm BOTH ambients for the whole conversation: the overlay so the ephemeral agent + its owned
        // skills resolve, and the envelope so the governor enforces the per-caller grant. Disposed in
        // reverse when the (materialised) conversation returns.
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

            return await mediator.Send(command, stoppingToken).ConfigureAwait(false);
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
