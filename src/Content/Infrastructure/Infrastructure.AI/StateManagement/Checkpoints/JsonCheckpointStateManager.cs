using Application.AI.Common.Interfaces.Compliance;
using Domain.Common.Config;
using Domain.Common.Config.Infrastructure;
using Domain.Common.Workflow;
using Infrastructure.AI.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Domain.Common.Compliance;

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
[PersonalDataStore(
    "JSON workflow-state files at {StateManagement:BasePath}/{workflowId}/checkpoints/, including free-form metadata supplied by callers. Records carry a workflow id, not an owner. A workflow's state is deleted once nothing has written it for StateManagement:Retention (90 days by default), or when the workflow is deleted.",
    PersonalDataCategories.Identifiers | PersonalDataCategories.DiagnosticText,
    PersonalDataKey.Unkeyed,
    RetentionBasis.Configured,
    ErasureSupport.ItemDeleteOnly,
    ExportSupport.NotSupported)]
public class JsonCheckpointStateManager : IStateManager, IStoreRetentionPeriod
{
    /// <summary>A workflow's state file, from the workflow's own folder; it marks the folder as a workflow.</summary>
    internal const string StateFileRelativePath = "checkpoints/workflow-state.json";

    private readonly ILogger<JsonCheckpointStateManager> _logger;
    private readonly IOptionsMonitor<InfrastructureConfig> _infraConfig;
    private readonly StateManagementConfig _settings;
    private readonly JsonSerializerOptions _jsonOptions;

    // Cache for state configurations (from AGENT.md files)
    private readonly Dictionary<string, StateConfiguration> _stateConfigCache = new();

    public JsonCheckpointStateManager(
        ILogger<JsonCheckpointStateManager> logger,
        IOptionsMonitor<InfrastructureConfig> infraConfig)
    {
        _logger = logger;
        _infraConfig = infraConfig;
        _settings = infraConfig.CurrentValue.StateManagement;

        // Configure JSON options for Agent Framework compatibility
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        // Ensure base path exists, owner-only (#640) -- also retroactively reasserted if it already
        // existed with looser permissions (#670).
        OwnerOnlyDirectoryHelper.Create(_settings.BasePath, _logger);
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

        if (!string.IsNullOrEmpty(directory))
            OwnerOnlyDirectoryHelper.Create(directory, _logger); // #640

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

        // Delegate to NodeState.GetMetadata, which correctly handles values that were
        // deserialized as JsonElement after a JSON checkpoint reload (this manager's own
        // LoadAsync produces exactly such values).
        return node.GetMetadata<T>(key);
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

    /// <inheritdoc />
    /// <remarks>A period below the sweep's floor is refused by the sweep, so it is reported as no period at all.</remarks>
    public TimeSpan? CurrentRetentionPeriod =>
        _infraConfig.CurrentValue.StateManagement.Retention is { Enabled: true } retention
        && retention.RetainFor >= RetentionPeriodConfig.MinimumRetainFor
            ? retention.RetainFor
            : null;

    /// <summary>
    /// Deletes the saved state of up to <paramref name="maxWorkflows"/> workflows nothing has written since
    /// <paramref name="cutoff"/> (#901): the JSON checkpoint, its temp file, and the markdown rendering
    /// (<see cref="MarkdownCheckpointDecorator.MarkdownRelativePath"/>) that describes the same state.
    /// </summary>
    /// <remarks>
    /// A workflow is a folder directly under the base path whose state file names that folder as its workflow id, as
    /// every state this store saves does; a folder that only holds a file of that name is not one, so a base path shared
    /// with other content cannot reach it. A workflow's age is its newest file; anything else in its folder is kept, and
    /// the state file goes last, so a delete a locked file interrupts is finished by the next sweep (see
    /// <see cref="RetentionDirectories"/>).
    /// </remarks>
    /// <returns>How many workflows' state was removed.</returns>
    public Task<int> DeleteWorkflowsNotWrittenSinceAsync(DateTimeOffset cutoff, int maxWorkflows, CancellationToken cancellationToken)
    {
        var deleted = 0;
        foreach (var workflow in RetentionDirectories.Subdirectories(Path.GetFullPath(_settings.BasePath)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (deleted >= maxWorkflows)
                break;

            try
            {
                if (!IsWorkflowSavedHere(workflow) || RetentionDirectories.NewestWriteUtc(workflow) >= cutoff.UtcDateTime)
                    continue;

                if (RetentionDirectories.DeleteOwnFiles(workflow, StateFileRelativePath, IsOwnFile))
                    deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not delete expired workflow state {Workflow}; will retry on the next sweep.", workflow);
            }
        }

        return Task.FromResult(deleted);
    }

    private static bool IsOwnFile(string relativePath) =>
        relativePath is StateFileRelativePath + ".tmp"
            or MarkdownCheckpointDecorator.MarkdownRelativePath
            or MarkdownCheckpointDecorator.MarkdownRelativePath + ".tmp";

    /// <summary>Whether the folder's state file names the folder's own workflow id.</summary>
    private bool IsWorkflowSavedHere(string workflow)
    {
        var stateFile = Path.Combine(workflow, StateFileRelativePath);
        if (!File.Exists(stateFile))
            return false;

        try
        {
            var state = JsonSerializer.Deserialize<WorkflowState>(File.ReadAllText(stateFile), _jsonOptions);
            return state is not null && state.WorkflowId == Path.GetFileName(workflow);
        }
        catch (JsonException)
        {
            return false;
        }
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
