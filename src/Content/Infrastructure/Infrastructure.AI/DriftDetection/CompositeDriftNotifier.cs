using Application.AI.Common.Interfaces.DriftDetection;
using Domain.AI.DriftDetection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.DriftDetection;

/// <summary>
/// Fans out drift notifications to all registered <see cref="IDriftNotificationChannel"/> implementations.
/// Individual channel failures are logged but do not block other channels.
/// </summary>
public sealed class CompositeDriftNotifier : IDriftNotifier
{
    private readonly IReadOnlyList<IDriftNotificationChannel> _channels;
    private readonly ILogger<CompositeDriftNotifier> _logger;

    public CompositeDriftNotifier(
        IEnumerable<IDriftNotificationChannel> channels,
        ILogger<CompositeDriftNotifier> logger)
    {
        _channels = channels.ToList();
        _logger = logger;
    }

    /// <inheritdoc />
    public Task NotifyDriftDetectedAsync(DriftScore score, CancellationToken ct) =>
        FanOutAsync(channel => channel.NotifyDriftDetectedAsync(score, ct));

    /// <inheritdoc />
    public Task NotifyDriftResolvedAsync(DriftEvent driftEvent, CancellationToken ct) =>
        FanOutAsync(channel => channel.NotifyDriftResolvedAsync(driftEvent, ct));

    private Task FanOutAsync(Func<IDriftNotificationChannel, Task> action)
    {
        var tasks = _channels.Select(channel => SafeNotifyAsync(action, channel));
        return Task.WhenAll(tasks);
    }

    private async Task SafeNotifyAsync(
        Func<IDriftNotificationChannel, Task> action,
        IDriftNotificationChannel channel)
    {
        try
        {
            await action(channel);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Drift notification channel {Channel} failed", channel.GetType().Name);
        }
    }
}
