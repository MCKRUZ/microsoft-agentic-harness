namespace Domain.Common.Workflow;

/// <summary>
/// Defines valid states and transitions for nodes in a workflow.
///
/// <para><b>Purpose:</b></para>
/// Encapsulates state machine logic that was previously hardcoded in enums.
/// Read from AGENT.md state_configuration section, making workflows fully configurable.
///
/// <para><b>Example from AGENT.md:</b></para>
/// <code>
/// state_configuration:
///   allowed_statuses:
///     - not_started
///     - in_progress
///     - paused
///     - completed
///     - failed
///
///   allowed_transitions:
///     not_started: [in_progress]
///     in_progress: [paused, completed, failed]
///     paused: [in_progress, failed]
///     completed: []
///     failed: [not_started]
///
///   initial_status: not_started
/// </code>
/// </summary>
public class StateConfiguration
{
    /// <summary>
    /// All valid status values for nodes in this workflow.
    /// Examples: "not_started", "in_progress", "paused", "completed", "failed"
    /// </summary>
    public List<string> AllowedStatuses { get; set; } = new();

    /// <summary>
    /// Valid state transitions.
    /// Key = current status, Value = list of statuses that can be transitioned to.
    ///
    /// <para><b>Example:</b></para>
    /// <code>
    /// {
    ///   ["not_started"] = ["in_progress"],
    ///   ["in_progress"] = ["paused", "completed", "failed"],
    ///   ["paused"] = ["in_progress", "failed"],
    ///   ["completed"] = [],  // Terminal state
    ///   ["failed"] = ["not_started"]  // Can retry
    /// }
    /// </code>
    /// </summary>
    public Dictionary<string, List<string>> AllowedTransitions { get; set; } = new();

    /// <summary>
    /// The status that nodes start with.
    /// Typically "not_started".
    /// </summary>
    public string InitialStatus { get; set; } = "not_started";

    /// <summary>
    /// Terminal states that cannot be transitioned out of.
    /// Examples: "completed", "cancelled"
    /// </summary>
    public List<string> TerminalStates { get; set; } = new();

    /// <summary>
    /// Checks if a transition from one status to another is allowed.
    /// </summary>
    public bool CanTransition(string fromStatus, string toStatus)
    {
        // Same status is always allowed (idempotent)
        if (fromStatus == toStatus)
            return true;

        // Check if the transition is defined
        if (AllowedTransitions.TryGetValue(fromStatus, out var allowed))
        {
            return allowed.Contains(toStatus);
        }

        return false;
    }

    /// <summary>
    /// Checks if a status is a terminal state.
    /// </summary>
    public bool IsTerminal(string status)
        => TerminalStates.Contains(status);

    /// <summary>
    /// Checks if a status value is valid.
    /// </summary>
    public bool IsValidStatus(string status)
        => AllowedStatuses.Contains(status);

    /// <summary>
    /// Gets all valid next statuses for a given current status.
    /// </summary>
    public List<string> GetValidTransitions(string fromStatus)
    {
        if (AllowedTransitions.TryGetValue(fromStatus, out var allowed))
        {
            return new List<string>(allowed);
        }
        return new List<string>();
    }

    /// <summary>
    /// Validates a state configuration and returns any errors.
    /// </summary>
    public List<string> Validate()
    {
        var errors = new List<string>();

        // Check that initial status is allowed
        if (!AllowedStatuses.Contains(InitialStatus))
            errors.Add($"Initial status '{InitialStatus}' is not in allowed_statuses");

        // Check that all transition targets are allowed statuses
        foreach (var (from, toList) in AllowedTransitions)
        {
            if (!AllowedStatuses.Contains(from))
                errors.Add($"Transition source '{from}' is not in allowed_statuses");

            foreach (var to in toList)
            {
                if (!AllowedStatuses.Contains(to))
                    errors.Add($"Transition target '{to}' from '{from}' is not in allowed_statuses");
            }
        }

        // Check that terminal states have no outgoing transitions
        foreach (var terminal in TerminalStates)
        {
            if (AllowedTransitions.TryGetValue(terminal, out var transitions) && transitions.Count > 0)
                errors.Add($"Terminal state '{terminal}' has outgoing transitions defined");
        }

        return errors;
    }
}
