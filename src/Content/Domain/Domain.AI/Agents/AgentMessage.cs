namespace Domain.AI.Agents;

/// <summary>
/// A message passed between agents via the mailbox system.
/// Enables asynchronous communication between orchestrator and subagents.
/// </summary>
public sealed record AgentMessage
{
    /// <summary>The sending agent's ID.</summary>
    public required string FromAgentId { get; init; }

    /// <summary>The receiving agent's ID.</summary>
    public required string ToAgentId { get; init; }

    /// <summary>The message content.</summary>
    public required string Content { get; init; }

    /// <summary>The type of message.</summary>
    public required AgentMessageType MessageType { get; init; }

    /// <summary>When the message was sent.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Correlation ID linking related messages (e.g., task request and its result).</summary>
    public required string CorrelationId { get; init; }
}
