namespace Domain.Common.Workflow;

/// <summary>
/// Interface for managing workflow state persistence.
///
/// <para><b>Purpose:</b></para>
/// Generic state management that works for ANY workflow domain.
/// Implementations can store state in markdown files, database, or any other storage.
///
/// <para><b>Generic Design:</b></para>
/// <list type="bullet">
///   <item><description>No hardcoded phases, activities, or statuses</description></item>
///   <item><description>All node IDs and statuses are strings</description></item>
///   <item><description>Metadata dictionary stores domain-specific values</description></item>
/// </list>
/// </summary>
public interface IStateManager
{
    /// <summary>
    /// Loads the complete workflow state from storage.
    /// Returns null if the workflow doesn't exist.
    /// </summary>
    Task<WorkflowState?> LoadAsync(string workflowId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the complete workflow state to storage.
    /// </summary>
    Task SaveAsync(WorkflowState state, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a workflow state exists in storage.
    /// </summary>
    Task<bool> ExistsAsync(string workflowId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new workflow with the given ID.
    /// Throws if workflow already exists.
    /// </summary>
    Task<WorkflowState> CreateAsync(string workflowId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a workflow state.
    /// Returns false if workflow doesn't exist.
    /// </summary>
    Task<bool> DeleteAsync(string workflowId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the state of a specific node within a workflow.
    /// Returns null if the node doesn't exist.
    /// </summary>
    Task<NodeState?> GetNodeStateAsync(string workflowId, string nodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates or creates a node state within a workflow.
    /// </summary>
    Task UpdateNodeStateAsync(string workflowId, string nodeId, NodeState state, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all nodes of a specific type within a workflow.
    /// </summary>
    Task<List<NodeState>> GetNodesByTypeAsync(string workflowId, string nodeType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a status transition is allowed.
    /// Uses state_configuration from the AGENT.md of the node.
    /// </summary>
    Task<bool> CanTransitionAsync(string workflowId, string nodeId, string toStatus, CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs a status transition for a node.
    /// Throws InvalidStateTransitionException if the transition is not allowed.
    /// </summary>
    Task TransitionAsync(string workflowId, string nodeId, string toStatus, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a metadata value for a node.
    /// </summary>
    Task SetMetadataAsync(string workflowId, string nodeId, string key, object value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a metadata value from a node.
    /// Returns default if the key doesn't exist.
    /// </summary>
    Task<T?> GetMetadataAsync<T>(string workflowId, string nodeId, string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all metadata for a node.
    /// </summary>
    Task<Dictionary<string, object>> GetAllMetadataAsync(string workflowId, string nodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a node is complete (status == "completed").
    /// </summary>
    Task<bool> IsNodeCompleteAsync(string workflowId, string nodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all incomplete (not completed) nodes in a workflow.
    /// </summary>
    Task<List<NodeState>> GetIncompleteNodesAsync(string workflowId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all completed nodes in a workflow.
    /// </summary>
    Task<List<NodeState>> GetCompletedNodesAsync(string workflowId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current node (the one marked as current in the workflow state).
    /// </summary>
    Task<NodeState?> GetCurrentNodeAsync(string workflowId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the current node for a workflow.
    /// </summary>
    Task SetCurrentNodeAsync(string workflowId, string nodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the workflow status.
    /// </summary>
    Task SetWorkflowStatusAsync(string workflowId, string status, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the workflow status.
    /// </summary>
    Task<string> GetWorkflowStatusAsync(string workflowId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes the workflow (sets status to completed and records completion time).
    /// </summary>
    Task CompleteWorkflowAsync(string workflowId, CancellationToken cancellationToken = default);
}
