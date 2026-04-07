namespace Domain.Common.Workflow;

/// <summary>
/// Represents the complete state of a workflow execution.
///
/// <para><b>Purpose:</b></para>
/// Generic state container that works for ANY workflow domain (SDLC, game dev, legal, etc.).
/// All domain-specific knowledge is extracted to AGENT.md and SKILL.md files.
///
/// <para><b>Generic Design:</b></para>
/// <list type="bullet">
///   <item><description>No hardcoded enums - all statuses are strings read from configuration</description></item>
///   <item><description>No hardcoded phase/activity types - all nodes are identified by string IDs</description></item>
///   <item><description>Metadata dictionary stores domain-specific metrics</description></item>
/// </list>
///
/// <para><b>Example SDLC workflow:</b></para>
/// <code>
/// var state = new WorkflowState
/// {
///     WorkflowId = "proj-001",
///     CurrentNodeId = "phase0-discovery",
///     WorkflowStatus = "in_progress",
///     Nodes = new Dictionary&lt;string, NodeState&gt;
/// };
/// </code>
/// </summary>
public class WorkflowState
{
    /// <summary>
    /// Unique identifier for this workflow execution.
    /// Examples: "proj-001", "run-20250112-001", "game-dev-v1"
    /// </summary>
    public string WorkflowId { get; set; } = string.Empty;

    /// <summary>
    /// ID of the currently active node in the workflow.
    /// Examples: "phase0-discovery", "discovery-intake", "stakeholder-interview"
    /// </summary>
    public string CurrentNodeId { get; set; } = string.Empty;

    /// <summary>
    /// Current status of the workflow.
    /// Examples: "not_started", "in_progress", "paused", "completed", "failed"
    /// Valid values are defined in AGENT.md state_configuration.allowed_statuses
    /// </summary>
    public string WorkflowStatus { get; set; } = "not_started";

    /// <summary>
    /// When the workflow was started.
    /// </summary>
    public DateTime WorkflowStarted { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the workflow was completed (null if still running).
    /// </summary>
    public DateTime? WorkflowCompleted { get; set; }

    /// <summary>
    /// All nodes in this workflow (phases, activities, skills, etc.).
    /// Key is the node ID (e.g., "discovery-intake", "stakeholder-discovery").
    /// </summary>
    public Dictionary<string, NodeState> Nodes { get; set; } = new();

    /// <summary>
    /// Additional metadata for the workflow.
    /// Can include:
    /// <list type="bullet">
    ///   <item><description>Domain name (e.g., "sdlc", "game-dev", "legal")</description></item>
    ///   <item><description>Workflow version</description></item>
    ///   <item><description>Custom configuration overrides</description></item>
    ///   <item><description>Aggregate metrics</description></item>
    /// </list>
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();

    /// <summary>
    /// Gets a node by ID, creating it if it doesn't exist.
    /// </summary>
    public NodeState GetOrCreateNode(string nodeId)
    {
        if (!Nodes.TryGetValue(nodeId, out var node))
        {
            node = new NodeState { NodeId = nodeId };
            Nodes[nodeId] = node;
        }
        return node;
    }

    /// <summary>
    /// Gets all nodes of a specific type.
    /// </summary>
    public List<NodeState> GetNodesByType(string nodeType)
        => Nodes.Values.Where(n => n.NodeType == nodeType).ToList();

    /// <summary>
    /// Gets all nodes with a specific status.
    /// </summary>
    public List<NodeState> GetNodesByStatus(string status)
        => Nodes.Values.Where(n => n.Status == status).ToList();

    /// <summary>
    /// Gets incomplete (not completed) nodes.
    /// </summary>
    public List<NodeState> GetIncompleteNodes()
        => Nodes.Values.Where(n => n.Status != "completed").ToList();

    /// <summary>
    /// Gets completed nodes.
    /// </summary>
    public List<NodeState> GetCompletedNodes()
        => Nodes.Values.Where(n => n.Status == "completed").ToList();

    /// <summary>
    /// Checks if a specific node is complete.
    /// </summary>
    public bool IsNodeComplete(string nodeId)
        => Nodes.TryGetValue(nodeId, out var node) && node.Status == "completed";

    /// <summary>
    /// Gets total iterations across all nodes.
    /// </summary>
    public int GetTotalIterationCount()
        => Nodes.Values.Sum(n => n.Iteration);

    /// <summary>
    /// Gets metadata value for a specific node.
    /// </summary>
    public T? GetNodeMetadata<T>(string nodeId, string key)
    {
        if (!Nodes.TryGetValue(nodeId, out var node))
            return default;

        if (!node.Metadata.TryGetValue(key, out var value))
            return default;

        if (value is T typed)
            return typed;

        // Try to convert
        try
        {
            return (T)Convert.ChangeType(value, typeof(T));
        }
        catch
        {
            return default;
        }
    }

    /// <summary>
    /// Sets metadata value for a specific node.
    /// </summary>
    public void SetNodeMetadata(string nodeId, string key, object value)
    {
        var node = GetOrCreateNode(nodeId);
        node.Metadata[key] = value;
    }
}
