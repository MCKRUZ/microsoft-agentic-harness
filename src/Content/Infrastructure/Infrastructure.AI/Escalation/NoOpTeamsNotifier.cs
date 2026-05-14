using Application.AI.Common.Interfaces.Escalation;
using Domain.AI.Escalation;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Escalation;

/// <summary>
/// No-op Microsoft Teams notification channel. Logs escalation events at Debug level
/// without delivering them. Replace with a real Teams webhook or Graph API adapter
/// for production use.
/// </summary>
/// <remarks>
/// Registered as an <see cref="IEscalationNotificationChannel"/> entry in DI.
/// The <see cref="CompositeEscalationNotifier"/> automatically discovers and
/// includes this channel in fan-out notifications.
/// </remarks>
public sealed class NoOpTeamsNotifier : IEscalationNotificationChannel
{
    private readonly ILogger<NoOpTeamsNotifier> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NoOpTeamsNotifier"/> class.
    /// </summary>
    /// <param name="logger">Logger for recording no-op notification events.</param>
    public NoOpTeamsNotifier(ILogger<NoOpTeamsNotifier> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Task NotifyEscalationRequestedAsync(EscalationRequest request, CancellationToken ct)
    {
        _logger.LogDebug("Teams: would notify escalation requested for {EscalationId}", request.EscalationId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task NotifyEscalationResolvedAsync(EscalationOutcome outcome, CancellationToken ct)
    {
        _logger.LogDebug("Teams: would notify escalation resolved for {EscalationId}", outcome.EscalationId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task NotifyEscalationExpiringAsync(EscalationRequest request, TimeSpan remaining, CancellationToken ct)
    {
        _logger.LogDebug("Teams: would notify escalation expiring for {EscalationId} ({Remaining} remaining)", request.EscalationId, remaining);
        return Task.CompletedTask;
    }
}
