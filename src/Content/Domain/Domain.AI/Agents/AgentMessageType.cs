namespace Domain.AI.Agents;

/// <summary>
/// Categorizes messages passed between agents via the mailbox system.
/// </summary>
public enum AgentMessageType
{
    /// <summary>A task assignment or request.</summary>
    Task,

    /// <summary>A task result or completion notification.</summary>
    Result,

    /// <summary>An informational notification.</summary>
    Notification,

    /// <summary>An error report.</summary>
    Error
}
