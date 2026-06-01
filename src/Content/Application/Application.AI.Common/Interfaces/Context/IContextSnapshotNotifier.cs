using Domain.AI.Context;

namespace Application.AI.Common.Interfaces.Context;

/// <summary>
/// Broadcasts a <see cref="ContextSnapshot"/> to interested clients (dashboard UI)
/// after a turn completes. Implementation is host-specific: the dashboard host
/// publishes via SignalR and persists to the observability store; the CLI host
/// uses a no-op since no clients are attached.
/// </summary>
/// <remarks>
/// <para>
/// Called by
/// <see cref="CQRS.Agents.ExecuteAgentTurn.ExecuteAgentTurnCommandHandler"/>
/// after each successful turn, AFTER the assistant message has been recorded.
/// </para>
/// <para>
/// Implementations MUST be safe for fire-and-forget invocation: failures
/// (transport hiccups, store outages) MUST be logged and swallowed — never
/// propagated. A dropped notification is acceptable; corrupting the upstream
/// turn-handler result over a transport flake is not. Mirrors the contract of
/// <see cref="Application.AI.Common.Evaluation.Interfaces.IEvalRunNotifier"/>.
/// </para>
/// </remarks>
public interface IContextSnapshotNotifier
{
    /// <summary>
    /// Notifies subscribers that <paramref name="snapshot"/> has been captured.
    /// </summary>
    /// <param name="snapshot">The snapshot to broadcast and (where applicable) persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task NotifyAsync(ContextSnapshot snapshot, CancellationToken cancellationToken);
}
