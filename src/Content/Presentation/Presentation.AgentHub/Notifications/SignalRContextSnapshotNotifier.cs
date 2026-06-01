using Application.AI.Common.Interfaces.Context;
using Domain.AI.Context;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Presentation.AgentHub.Extensions;
using Presentation.AgentHub.Hubs;

namespace Presentation.AgentHub.Notifications;

/// <summary>
/// Broadcasts <see cref="AgentTelemetryHub.EventContextSnapshot"/> to the
/// per-conversation SignalR group so a live Dashboard session updates the
/// hero context bar, scrub strip, and drawer per turn without polling.
/// Mirrors the <see cref="SignalREvalRunNotifier"/> contract: broadcast-only,
/// fire-and-forget, swallow transport failures. Persistence is the upstream
/// handler's concern (<c>IObservabilityStore.RecordContextSnapshotAsync</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Contract — payload property names are part of the SignalR wire contract.</b>
/// The JS client subscribes via
/// <c>connection.on("ContextSnapshot", payload =&gt; payload.conversationId)</c>;
/// renaming any property silently breaks the dashboard. The shape is pinned in
/// <c>SignalRContextSnapshotNotifierTests</c>.
/// </para>
/// </remarks>
public sealed class SignalRContextSnapshotNotifier : IContextSnapshotNotifier
{
    private readonly IHubContext<AgentTelemetryHub> _hub;
    private readonly ILogger<SignalRContextSnapshotNotifier> _logger;

    /// <summary>Initializes a new instance.</summary>
    public SignalRContextSnapshotNotifier(
        IHubContext<AgentTelemetryHub> hub,
        ILogger<SignalRContextSnapshotNotifier> logger)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(logger);

        _hub = hub;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task NotifyAsync(ContextSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var payload = snapshot.ToDto();

        try
        {
            await _hub.Clients
                .Group(AgentTelemetryHub.ConversationGroup(snapshot.ConversationId))
                .SendAsync(AgentTelemetryHub.EventContextSnapshot, payload, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to broadcast ContextSnapshot for conversation {ConversationId} turn {TurnIndex}.",
                snapshot.ConversationId,
                snapshot.TurnIndex);
            // Swallow per IContextSnapshotNotifier contract: notification failures
            // must not corrupt the upstream turn-handler success.
        }
    }
}
