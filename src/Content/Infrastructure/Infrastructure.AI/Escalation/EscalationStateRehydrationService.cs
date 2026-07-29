using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Escalation;

/// <summary>
/// One-shot startup step that rehydrates durably persisted pending escalations into the
/// active set of <see cref="DefaultEscalationService"/>, so approvals opened before a restart
/// remain decidable, listable, and cancellable afterwards.
/// </summary>
/// <remarks>
/// <para>
/// Registered unconditionally; with durable escalation state disabled (the default) the
/// service's Null state store yields zero records and this is a silent no-op with zero I/O.
/// </para>
/// <para>
/// A scan-level failure is logged at <c>Critical</c> and swallowed rather than propagated.
/// Individual bad rows are already quarantined inside the store, but one residual case cannot
/// be: the row's Guid key is converted during query materialization, outside any per-row guard,
/// so a single truncated key blob fails the whole scan. Letting that propagate would turn one
/// corrupt byte into a total availability loss — the host would refuse to boot at all. Booting
/// with escalations unrestored is bad and must be screamed about; not booting is worse.
/// </para>
/// </remarks>
public sealed class EscalationStateRehydrationService : IHostedService
{
    private readonly DefaultEscalationService _escalationService;
    private readonly ILogger<EscalationStateRehydrationService> _logger;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    /// <param name="escalationService">
    /// The concrete escalation service to rehydrate. Depends on the concrete type (not
    /// <c>IEscalationService</c>) because rehydration is an implementation detail of the
    /// default service, not part of the escalation contract.
    /// </param>
    /// <param name="logger">Structured logger.</param>
    public EscalationStateRehydrationService(
        DefaultEscalationService escalationService,
        ILogger<EscalationStateRehydrationService> logger)
    {
        ArgumentNullException.ThrowIfNull(escalationService);
        ArgumentNullException.ThrowIfNull(logger);
        _escalationService = escalationService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var restored = await _escalationService.RehydratePendingEscalationsAsync(cancellationToken);
            if (restored > 0)
            {
                _logger.LogInformation(
                    "Restored {RestoredCount} pending escalation(s) from the durable governance-state store",
                    restored);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // See the class remarks: a scan-level failure must not be a full-availability DoS.
            // The reconciler's scheduled passes retry the scan, so a transient fault is
            // self-healing; a persistent one leaves this Critical line every boot.
            _logger.LogCritical(ex,
                "Failed to rehydrate durable escalation state at startup. Pending approvals from before " +
                "this restart are NOT restored and will not appear in pending lists until the underlying " +
                "fault is fixed. The host is continuing to start; investigate the governance-state database");
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
