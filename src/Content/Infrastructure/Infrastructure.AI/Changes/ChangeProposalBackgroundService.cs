using Application.AI.Common.Interfaces.Changes;
using Domain.Common.Helpers;
using Domain.AI.Changes;
using Domain.Common.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Changes;

/// <summary>
/// Drains the <see cref="IChangeProposalDispatchQueue"/> and invokes
/// <see cref="IChangeProposalOrchestrator.ProcessAsync"/> for each enqueued
/// proposal id. Decouples the Submit / Approve command handlers from the
/// pipeline's wall-clock cost so HTTP requests don't block on slow gates.
/// </summary>
/// <remarks>
/// <para>
/// One scope per dispatched id. The orchestrator and its dependencies are
/// resolved from <see cref="IServiceScope.ServiceProvider"/> so consumers who
/// replace the in-memory store with a scoped (e.g. EF Core) implementation
/// don't need to also re-lifetime the orchestrator — captive-dependency
/// risk is avoided by resolving everything at dispatch time.
/// </para>
/// <para>
/// Failure isolation. The orchestrator already converts gate exceptions to
/// terminal <c>Rejected</c> transitions; this service catches anything that
/// escapes that contract (catastrophic logic bugs, DI resolution failures,
/// store I/O exceptions) and logs at error, then keeps draining. One bad
/// proposal must not stall the queue for every subsequent proposal. The
/// orchestrator's resume-from-current-status logic lets a partially-processed
/// proposal pick up next time something re-enqueues it.
/// </para>
/// <para>
/// Cancellation. The service honors <see cref="BackgroundService.StopAsync(CancellationToken)"/>
/// — when the host begins shutdown the loop exits as soon as the current
/// orchestrator call returns (or itself observes the cancellation). Ids
/// remaining in the channel are lost; an at-least-once consumer wiring an
/// outbox-backed <see cref="IChangeProposalDispatchQueue"/> handles this
/// at the queue layer.
/// </para>
/// </remarks>
public sealed class ChangeProposalBackgroundService : BackgroundService
{
    private readonly IChangeProposalDispatchQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<ChangeProposalBackgroundService> _logger;

    /// <summary>Initializes a new <see cref="ChangeProposalBackgroundService"/>.</summary>
    public ChangeProposalBackgroundService(
        IChangeProposalDispatchQueue queue,
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<AppConfig> config,
        ILogger<ChangeProposalBackgroundService> logger)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _queue = queue;
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var proposalId in _queue.DequeueAllAsync(stoppingToken).ConfigureAwait(false))
        {
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await DispatchOneAsync(proposalId, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task DispatchOneAsync(string proposalId, CancellationToken stoppingToken)
    {
        var mode = ParseMode(_config.CurrentValue.AI.Changes.DefaultMode);

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<IChangeProposalOrchestrator>();
            await orchestrator.ProcessAsync(proposalId, mode, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown — propagate by exiting the loop.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "ChangeProposal {ProposalId} dispatch failed and was dropped; " +
                "the proposal stays in its current status and can be re-driven by re-enqueueing.",
                proposalId);
        }
    }

    // Name-only, matching ChangeProposalCommandHelper.ParseMode exactly — see the reasoning there.
    // MergeGate short-circuits on `Mode == Shadow`, so anything that is not exactly Shadow performs a
    // real apply; the Shadow fallback only protects against a typo if the parse can reject one.
    private static OrchestratorMode ParseMode(string raw) =>
        EnumNameHelper.TryParseName<OrchestratorMode>(raw, out var mode) ? mode : OrchestratorMode.Shadow;
}
