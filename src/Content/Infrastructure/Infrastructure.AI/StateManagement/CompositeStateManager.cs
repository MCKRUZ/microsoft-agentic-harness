using Domain.Common.Config;
using Domain.Common.Workflow;
using Infrastructure.AI.Generators;
using Infrastructure.AI.StateManagement.Checkpoints;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.StateManagement;

/// <summary>
/// Composite state manager that combines JSON checkpointing with markdown generation.
/// </summary>
/// <remarks>
/// <para><b>Purpose:</b></para>
/// Entry point that combines both state persistence mechanisms:
/// - JSON (via JsonCheckpointStateManager) for machine efficiency and Agent Framework resume
/// - Markdown (via MarkdownCheckpointDecorator) for human consumption and version control
///
/// <para><b>Architecture:</b></para>
/// This class uses the decorator pattern internally:
/// <code>
/// CompositeStateManager
///     └── MarkdownCheckpointDecorator
///         ├── JsonCheckpointStateManager (JSON - primary for machines)
///         └── StateMarkdownGenerator (Markdown - primary for humans)
/// </code>
///
/// <para><b>Both Representations are Primary:</b></para>
/// - JSON: Used by Agent Framework for workflow resume, efficient programmatic access
/// - Markdown: Human-readable, git-friendly, documentation
///
/// <para><b>Usage:</b></para>
/// Register in DI as the default IStateManager when StateStorageType is
/// AgentFrameworkWithMarkdown. Both JSON and markdown files are created/updated
/// on every save operation.
/// </remarks>
public class CompositeStateManager : IStateManager
{
    private readonly IStateManager _decoratedManager;

    /// <summary>
    /// Initializes a new instance of the CompositeStateManager.
    /// </summary>
    /// <param name="logger">Logger for operations</param>
    /// <param name="markdownGenerator">Generator for markdown output</param>
    /// <param name="appConfig">Application configuration</param>
    public CompositeStateManager(
        ILogger<CompositeStateManager> logger,
        IStateMarkdownGenerator markdownGenerator,
        IOptionsMonitor<AppConfig> appConfig)
    {
        var settings = appConfig.CurrentValue.Infrastructure.StateManagement;

        // Create the inner manager (JSON checkpointing) with its own logger type
        var afLogger = NullLogger<JsonCheckpointStateManager>.Instance;
        var inner = new JsonCheckpointStateManager(
            NullLogger<JsonCheckpointStateManager>.Instance,
            appConfig);

        // Only wrap with decorator if markdown generation is enabled
        if (settings.EnableMarkdownGeneration)
        {
            // Wrap with decorator (adds markdown generation) with its own logger type
            var decoratorLogger = NullLogger<MarkdownCheckpointDecorator>.Instance;
            _decoratedManager = new MarkdownCheckpointDecorator(
                inner,
                decoratorLogger,
                markdownGenerator,
                appConfig);
        }
        else
        {
            _decoratedManager = inner;
        }
    }

    /// <summary>
    /// Initializes a new instance of the CompositeStateManager with a pre-configured inner manager.
    /// </summary>
    /// <param name="inner">The inner state manager to wrap with markdown generation</param>
    /// <param name="logger">Logger for operations</param>
    /// <param name="markdownGenerator">Generator for markdown output</param>
    /// <param name="appConfig">Application configuration</param>
    public CompositeStateManager(
        IStateManager inner,
        ILogger<CompositeStateManager> logger,
        IStateMarkdownGenerator markdownGenerator,
        IOptionsMonitor<AppConfig> appConfig)
    {
        var settings = appConfig.CurrentValue.Infrastructure.StateManagement;

        // Only wrap with decorator if markdown generation is enabled
        if (settings.EnableMarkdownGeneration)
        {
            // Wrap the provided inner manager with markdown decorator
            var decoratorLogger = NullLogger<MarkdownCheckpointDecorator>.Instance;
            _decoratedManager = new MarkdownCheckpointDecorator(
                inner,
                decoratorLogger,
                markdownGenerator,
                appConfig);
        }
        else
        {
            _decoratedManager = inner;
        }
    }

    public Task<WorkflowState?> LoadAsync(string workflowId, CancellationToken cancellationToken = default)
        => _decoratedManager.LoadAsync(workflowId, cancellationToken);

    public Task SaveAsync(WorkflowState state, CancellationToken cancellationToken = default)
        => _decoratedManager.SaveAsync(state, cancellationToken);

    public Task<bool> ExistsAsync(string workflowId, CancellationToken cancellationToken = default)
        => _decoratedManager.ExistsAsync(workflowId, cancellationToken);

    public Task<WorkflowState> CreateAsync(string workflowId, CancellationToken cancellationToken = default)
        => _decoratedManager.CreateAsync(workflowId, cancellationToken);

    public Task<bool> DeleteAsync(string workflowId, CancellationToken cancellationToken = default)
        => _decoratedManager.DeleteAsync(workflowId, cancellationToken);

    public Task<NodeState?> GetNodeStateAsync(string workflowId, string nodeId, CancellationToken cancellationToken = default)
        => _decoratedManager.GetNodeStateAsync(workflowId, nodeId, cancellationToken);

    public Task UpdateNodeStateAsync(string workflowId, string nodeId, NodeState state, CancellationToken cancellationToken = default)
        => _decoratedManager.UpdateNodeStateAsync(workflowId, nodeId, state, cancellationToken);

    public Task<List<NodeState>> GetNodesByTypeAsync(string workflowId, string nodeType, CancellationToken cancellationToken = default)
        => _decoratedManager.GetNodesByTypeAsync(workflowId, nodeType, cancellationToken);

    public Task<bool> CanTransitionAsync(string workflowId, string nodeId, string toStatus, CancellationToken cancellationToken = default)
        => _decoratedManager.CanTransitionAsync(workflowId, nodeId, toStatus, cancellationToken);

    public Task TransitionAsync(string workflowId, string nodeId, string toStatus, CancellationToken cancellationToken = default)
        => _decoratedManager.TransitionAsync(workflowId, nodeId, toStatus, cancellationToken);

    public Task SetMetadataAsync(string workflowId, string nodeId, string key, object value, CancellationToken cancellationToken = default)
        => _decoratedManager.SetMetadataAsync(workflowId, nodeId, key, value, cancellationToken);

    public Task<T?> GetMetadataAsync<T>(string workflowId, string nodeId, string key, CancellationToken cancellationToken = default)
        => _decoratedManager.GetMetadataAsync<T>(workflowId, nodeId, key, cancellationToken);

    public Task<Dictionary<string, object>> GetAllMetadataAsync(string workflowId, string nodeId, CancellationToken cancellationToken = default)
        => _decoratedManager.GetAllMetadataAsync(workflowId, nodeId, cancellationToken);

    public Task<bool> IsNodeCompleteAsync(string workflowId, string nodeId, CancellationToken cancellationToken = default)
        => _decoratedManager.IsNodeCompleteAsync(workflowId, nodeId, cancellationToken);

    public Task<List<NodeState>> GetIncompleteNodesAsync(string workflowId, CancellationToken cancellationToken = default)
        => _decoratedManager.GetIncompleteNodesAsync(workflowId, cancellationToken);

    public Task<List<NodeState>> GetCompletedNodesAsync(string workflowId, CancellationToken cancellationToken = default)
        => _decoratedManager.GetCompletedNodesAsync(workflowId, cancellationToken);

    public Task<NodeState?> GetCurrentNodeAsync(string workflowId, CancellationToken cancellationToken = default)
        => _decoratedManager.GetCurrentNodeAsync(workflowId, cancellationToken);

    public Task SetCurrentNodeAsync(string workflowId, string nodeId, CancellationToken cancellationToken = default)
        => _decoratedManager.SetCurrentNodeAsync(workflowId, nodeId, cancellationToken);

    public Task SetWorkflowStatusAsync(string workflowId, string status, CancellationToken cancellationToken = default)
        => _decoratedManager.SetWorkflowStatusAsync(workflowId, status, cancellationToken);

    public Task<string> GetWorkflowStatusAsync(string workflowId, CancellationToken cancellationToken = default)
        => _decoratedManager.GetWorkflowStatusAsync(workflowId, cancellationToken);

    public Task CompleteWorkflowAsync(string workflowId, CancellationToken cancellationToken = default)
        => _decoratedManager.CompleteWorkflowAsync(workflowId, cancellationToken);
}
