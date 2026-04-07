using Domain.Common.Config;
using Domain.Common.Config.Infrastructure;
using Domain.Common.Workflow;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Infrastructure.AI.StateManagement.Checkpoints;

/// <summary>
/// State manager using JSON checkpointing compatible with Microsoft Agent Framework.
/// </summary>
/// <remarks>
/// This implementation uses JSON files for state persistence, which are:
/// - Machine-readable and efficient for programmatic access
/// - Compatible with Agent Framework's workflow resume capabilities
/// - Located at: {BasePath}/{workflowId}/checkpoints/workflow-state.json
///
/// <para><b>JSON Format:</b></para>
/// <code>
/// {
///   "workflow_id": "proj-001",
///   "current_node_id": "phase0-discovery",
///   "workflow_status": "in_progress",
///   "workflow_started": "2025-01-10T09:00:00Z",
///   "nodes": {
///     "phase0-discovery": {
///       "node_id": "phase0-discovery",
///       "node_type": "phase",
///       "status": "in_progress",
///       ...
///     }
///   }
/// }
/// </code>
/// </remarks>
public class JsonCheckpointStateManager : IStateManager
{
    private readonly ILogger<JsonCheckpointStateManager> _logger;
    private readonly StateManagementConfig _settings;
    private readonly JsonSerializerOptions _jsonOptions;

    // Cache for state configurations (from AGENT.md files)
    private readonly Dictionary<string, StateConfiguration> _stateConfigCache = new();

    public JsonCheckpointStateManager(
        ILogger<JsonCheckpointStateManager> logger,
        IOptionsMonitor<AppConfig> appConfig)
    {
        _logger = logger;
        _settings = appConfig.CurrentValue.Infrastructure.StateManagement;

        // Configure JSON options for Agent Framework compatibility
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        // Ensure base path exists
        if (!Directory.Exists(_settings.BasePath))
            Directory.CreateDirectory(_settings.BasePath);
    }

    public async Task<WorkflowState?> LoadAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        var stateFilePath = GetStateFilePath(workflowId);

        if (!File.Exists(stateFilePath))
        {
            _logger.LogDebug("Workflow state file not found: {WorkflowId}", workflowId);
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(stateFilePath, cancellationToken);
            return JsonSerializer.Deserialize<WorkflowState>(json, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load workflow state: {WorkflowId}", workflowId);
            throw;
        }
    }

    public async Task SaveAsync(WorkflowState state, CancellationToken cancellationToken = default)
    {
        var stateFilePath = GetStateFilePath(state.WorkflowId);
        var directory = Path.GetDirectoryName(stateFilePath);

        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        // Write to temp file first for atomic operation
        var tempFilePath = stateFilePath + ".tmp";

        try
        {
            var json = JsonSerializer.Serialize(state, _jsonOptions);
            await File.WriteAllTextAsync(tempFilePath, json, cancellationToken);

            // Atomic replace
            if (File.Exists(stateFilePath))
                File.Delete(stateFilePath);

            File.Move(tempFilePath, stateFilePath);

            _logger.LogDebug("Saved workflow state (JSON): {WorkflowId}", state.WorkflowId);
        }
        catch
        {
            // Clean up temp file on failure
            if (File.Exists(tempFilePath))
                File.Delete(tempFilePath);
            throw;
        }
    }

    public Task<bool> ExistsAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        var stateFilePath = GetStateFilePath(workflowId);
        return Task.FromResult(File.Exists(stateFilePath));
    }

    public async Task<WorkflowState> CreateAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        if (await ExistsAsync(workflowId, cancellationToken))
            throw new InvalidOperationException($"Workflow '{workflowId}' already exists");

        var state = new WorkflowState
        {
            WorkflowId = workflowId,
            WorkflowStatus = "not_started",
            WorkflowStarted = DateTime.UtcNow
        };

        await SaveAsync(state, cancellationToken);
        return state;
    }

    public async Task<bool> DeleteAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        var stateFilePath = GetStateFilePath(workflowId);

        if (!File.Exists(stateFilePath))
            return false;

        File.Delete(stateFilePath);
        _logger.LogInformation("Deleted workflow state (JSON): {WorkflowId}", workflowId);
        return true;
    }

    public async Task<NodeState?> GetNodeStateAsync(string workflowId, string nodeId, CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(workflowId, cancellationToken);
        return state?.Nodes.TryGetValue(nodeId, out var node) == true ? node : null;
    }

    public async Task UpdateNodeStateAsync(string workflowId, string nodeId, NodeState state, CancellationToken cancellationToken = default)
    {
        var workflowState = await LoadAsync(workflowId, cancellationToken)
            ?? throw new InvalidOperationException($"Workflow '{workflowId}' not found");

        workflowState.Nodes[nodeId] = state;
        await SaveAsync(workflowState, cancellationToken);
    }

    public async Task<List<NodeState>> GetNodesByTypeAsync(string workflowId, string nodeType, CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(workflowId, cancellationToken);
        return state?.Nodes.Values.Where(n => n.NodeType == nodeType).ToList() ?? new List<NodeState>();
    }

    public async Task<bool> CanTransitionAsync(string workflowId, string nodeId, string toStatus, CancellationToken cancellationToken = default)
    {
        var node = await GetNodeStateAsync(workflowId, nodeId, cancellationToken);
        if (node == null)
            return false; // Node doesn't exist, can't transition

        var fromStatus = node.Status;

        // Get state configuration from the node's AGENT.md (if it's a phase) or parent phase
        var config = await GetStateConfigurationAsync(workflowId, nodeId, cancellationToken);
        return config.CanTransition(fromStatus, toStatus);
    }

    public async Task TransitionAsync(string workflowId, string nodeId, string toStatus, CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(workflowId, cancellationToken)
            ?? throw new InvalidOperationException($"Workflow '{workflowId}' not found");

        if (!state.Nodes.TryGetValue(nodeId, out var node))
            throw new InvalidOperationException($"Node '{nodeId}' not found in workflow '{workflowId}'");

        var config = await GetStateConfigurationAsync(workflowId, nodeId, cancellationToken);

        if (!config.CanTransition(node.Status, toStatus))
            throw new InvalidStateTransitionException(nodeId, node.Status, toStatus);

        // Update timestamps
        if (toStatus == "in_progress" && node.StartedAt == null)
            node.StartedAt = DateTime.UtcNow;

        if ((toStatus == "completed" || toStatus == "failed") && node.CompletedAt == null)
            node.CompletedAt = DateTime.UtcNow;

        node.Status = toStatus;
        await SaveAsync(state, cancellationToken);
    }

    public async Task SetMetadataAsync(string workflowId, string nodeId, string key, object value, CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(workflowId, cancellationToken)
            ?? throw new InvalidOperationException($"Workflow '{workflowId}' not found");

        if (!state.Nodes.TryGetValue(nodeId, out var node))
            throw new InvalidOperationException($"Node '{nodeId}' not found in workflow '{workflowId}'");

        node.Metadata[key] = value;
        await SaveAsync(state, cancellationToken);
    }

    public async Task<T?> GetMetadataAsync<T>(string workflowId, string nodeId, string key, CancellationToken cancellationToken = default)
    {
        var node = await GetNodeStateAsync(workflowId, nodeId, cancellationToken);
        if (node == null)
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

    public async Task<Dictionary<string, object>> GetAllMetadataAsync(string workflowId, string nodeId, CancellationToken cancellationToken = default)
    {
        var node = await GetNodeStateAsync(workflowId, nodeId, cancellationToken);
        return node?.Metadata ?? new Dictionary<string, object>();
    }

    public async Task<bool> IsNodeCompleteAsync(string workflowId, string nodeId, CancellationToken cancellationToken = default)
    {
        var node = await GetNodeStateAsync(workflowId, nodeId, cancellationToken);
        return node?.Status == "completed";
    }

    public async Task<List<NodeState>> GetIncompleteNodesAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(workflowId, cancellationToken);
        return state?.GetIncompleteNodes() ?? new List<NodeState>();
    }

    public async Task<List<NodeState>> GetCompletedNodesAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(workflowId, cancellationToken);
        return state?.GetCompletedNodes() ?? new List<NodeState>();
    }

    public async Task<NodeState?> GetCurrentNodeAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(workflowId, cancellationToken);
        if (state == null)
            return null;

        return state.Nodes.TryGetValue(state.CurrentNodeId, out var node) ? node : null;
    }

    public async Task SetCurrentNodeAsync(string workflowId, string nodeId, CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(workflowId, cancellationToken)
            ?? throw new InvalidOperationException($"Workflow '{workflowId}' not found");

        state.CurrentNodeId = nodeId;
        await SaveAsync(state, cancellationToken);
    }

    public async Task SetWorkflowStatusAsync(string workflowId, string status, CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(workflowId, cancellationToken)
            ?? throw new InvalidOperationException($"Workflow '{workflowId}' not found");

        state.WorkflowStatus = status;

        if (status == "completed")
            state.WorkflowCompleted = DateTime.UtcNow;

        await SaveAsync(state, cancellationToken);
    }

    public async Task<string> GetWorkflowStatusAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(workflowId, cancellationToken);
        return state?.WorkflowStatus ?? "unknown";
    }

    public async Task CompleteWorkflowAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        await SetWorkflowStatusAsync(workflowId, "completed", cancellationToken);
    }

    private string GetStateFilePath(string workflowId)
    {
        ValidatePathSegment(workflowId, nameof(workflowId));
        return Path.Combine(_settings.BasePath, workflowId, "checkpoints", "workflow-state.json");
    }

    /// <summary>
    /// Validates that a path segment does not contain directory traversal sequences
    /// or invalid path characters that could escape the base path.
    /// </summary>
    private static void ValidatePathSegment(string segment, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segment, paramName);

        if (segment.Contains("..") ||
            segment.Contains('/') ||
            segment.Contains('\\') ||
            segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException(
                $"Value contains invalid path characters or traversal sequences.", paramName);
        }
    }

    private Task<StateConfiguration> GetStateConfigurationAsync(string workflowId, string nodeId, CancellationToken cancellationToken)
    {
        // For JsonCheckpointStateManager, we use a simplified state configuration
        // In a full implementation, this could load from AGENT.md files like MarkdownStateManager
        // For now, return a default configuration

        var cacheKey = $"{workflowId}:{nodeId}";
        if (_stateConfigCache.TryGetValue(cacheKey, out var cached))
            return Task.FromResult(cached);

        var defaultConfig = new StateConfiguration
        {
            AllowedStatuses = new List<string> { "not_started", "in_progress", "completed", "failed" },
            AllowedTransitions = new Dictionary<string, List<string>>
            {
                ["not_started"] = new List<string> { "in_progress" },
                ["in_progress"] = new List<string> { "completed", "failed" },
                ["completed"] = new List<string>(),
                ["failed"] = new List<string> { "not_started" }
            },
            InitialStatus = "not_started",
            TerminalStates = new List<string> { "completed" }
        };

        _stateConfigCache[cacheKey] = defaultConfig;
        return Task.FromResult(defaultConfig);
    }
}
