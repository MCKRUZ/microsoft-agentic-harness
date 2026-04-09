using Domain.AI.Agents;

namespace Application.AI.Common.Interfaces.Agents;

/// <summary>
/// Asynchronous message passing between agents. Enables orchestrators to delegate
/// tasks and receive results without tight coupling.
/// </summary>
public interface IAgentMailbox
{
    /// <summary>Sends a message to an agent's inbox.</summary>
    /// <param name="message">The message to deliver.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendAsync(AgentMessage message, CancellationToken cancellationToken = default);

    /// <summary>Receives all pending messages for an agent (non-blocking).</summary>
    /// <param name="agentId">The target agent's identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All pending messages, or an empty list if none are available.</returns>
    Task<IReadOnlyList<AgentMessage>> ReceiveAsync(
        string agentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits for a response message matching the correlation ID, with timeout.
    /// </summary>
    /// <param name="agentId">The agent waiting for a response.</param>
    /// <param name="correlationId">The correlation ID to match against.</param>
    /// <param name="timeout">Maximum time to wait before returning null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching message, or null if the timeout elapses.</returns>
    Task<AgentMessage?> WaitForResponseAsync(
        string agentId,
        string correlationId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
