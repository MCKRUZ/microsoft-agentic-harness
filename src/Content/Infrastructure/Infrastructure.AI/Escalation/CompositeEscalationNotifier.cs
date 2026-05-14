using Application.AI.Common.Interfaces.Escalation;
using Domain.AI.Escalation;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Escalation;

/// <summary>
/// Fans out escalation notifications to all registered <see cref="IEscalationNotificationChannel"/> instances.
/// Individual channel failures are caught and logged without blocking other channels.
/// </summary>
/// <remarks>
/// Registered as the single <see cref="IEscalationNotifier"/> implementation.
/// To add a new delivery channel, implement <see cref="IEscalationNotificationChannel"/>
/// and register it in DI — the composite discovers channels automatically.
/// </remarks>
public sealed class CompositeEscalationNotifier : IEscalationNotifier
{
    private readonly IReadOnlyList<IEscalationNotificationChannel> _channels;
    private readonly ILogger<CompositeEscalationNotifier> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CompositeEscalationNotifier"/> class.
    /// </summary>
    /// <param name="channels">All registered notification channels discovered via DI.</param>
    /// <param name="logger">Logger for recording channel failures.</param>
    public CompositeEscalationNotifier(
        IEnumerable<IEscalationNotificationChannel> channels,
        ILogger<CompositeEscalationNotifier> logger)
    {
        _channels = channels.ToList();
        _logger = logger;
    }

    /// <inheritdoc />
    public Task NotifyEscalationRequestedAsync(EscalationRequest request, CancellationToken ct)
    {
        return FanOutAsync(
            channel => channel.NotifyEscalationRequestedAsync(request, ct));
    }

    /// <inheritdoc />
    public Task NotifyEscalationResolvedAsync(EscalationOutcome outcome, CancellationToken ct)
    {
        return FanOutAsync(
            channel => channel.NotifyEscalationResolvedAsync(outcome, ct));
    }

    /// <inheritdoc />
    public Task NotifyEscalationExpiringAsync(EscalationRequest request, TimeSpan remaining, CancellationToken ct)
    {
        return FanOutAsync(
            channel => channel.NotifyEscalationExpiringAsync(request, remaining, ct));
    }

    private Task FanOutAsync(Func<IEscalationNotificationChannel, Task> action)
    {
        var tasks = _channels.Select(channel => SafeNotifyAsync(action, channel));
        return Task.WhenAll(tasks);
    }

    private async Task SafeNotifyAsync(
        Func<IEscalationNotificationChannel, Task> action,
        IEscalationNotificationChannel channel)
    {
        try
        {
            await action(channel);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Notification channel {Channel} failed", channel.GetType().Name);
        }
    }
}
