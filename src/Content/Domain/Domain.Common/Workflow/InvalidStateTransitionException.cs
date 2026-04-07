namespace Domain.Common.Workflow;

/// <summary>
/// Exception thrown when an invalid state transition is attempted.
/// </summary>
public class InvalidStateTransitionException : Exception
{
    /// <summary>
    /// The current status before the attempted transition.
    /// </summary>
    public string FromStatus { get; }

    /// <summary>
    /// The status that was attempted to transition to.
    /// </summary>
    public string ToStatus { get; }

    /// <summary>
    /// The node ID for which the transition was attempted.
    /// </summary>
    public string NodeId { get; }

    public InvalidStateTransitionException(string nodeId, string fromStatus, string toStatus)
        : base($"Invalid state transition for node '{nodeId}': '{fromStatus}' -> '{toStatus}'")
    {
        NodeId = nodeId;
        FromStatus = fromStatus;
        ToStatus = toStatus;
    }

    public InvalidStateTransitionException(string nodeId, string fromStatus, string toStatus, string message)
        : base($"Invalid state transition for node '{nodeId}': '{fromStatus}' -> '{toStatus}'. {message}")
    {
        NodeId = nodeId;
        FromStatus = fromStatus;
        ToStatus = toStatus;
    }
}
