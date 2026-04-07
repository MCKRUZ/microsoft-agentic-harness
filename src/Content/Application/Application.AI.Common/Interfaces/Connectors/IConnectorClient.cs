namespace Application.AI.Common.Interfaces.Connectors;

/// <summary>
/// Unified interface for external system connector clients.
/// Connector clients provide standardized, operation-based access to external APIs
/// (GitHub, Jira, Azure DevOps, Slack, etc.) that the agent can invoke as tools.
/// </summary>
/// <remarks>
/// Connector clients follow these principles:
/// <list type="bullet">
///   <item><description><strong>Operation-based:</strong> Execute discrete operations with parameters</description></item>
///   <item><description><strong>Result-oriented:</strong> Return structured results (success/failure/data)</description></item>
///   <item><description><strong>Availability-aware:</strong> Check configuration before attempting operations</description></item>
///   <item><description><strong>Error-resilient:</strong> Handle failures gracefully with clear messages</description></item>
/// </list>
///
/// <para><b>Example Implementation:</b></para>
/// <code>
/// public class GitHubIssuesConnector : ConnectorClientBase
/// {
///     public override string ToolName => "github_issues";
///
///     protected override async Task&lt;ConnectorOperationResult&gt; ExecuteOperationAsync(
///         string operation,
///         Dictionary&lt;string, object&gt; parameters,
///         CancellationToken cancellationToken)
///     {
///         return operation switch
///         {
///             "create_issue" => await CreateIssueAsync(parameters, cancellationToken),
///             "list_issues" => await ListIssuesAsync(parameters, cancellationToken),
///             _ => ConnectorOperationResult.Failure($"Operation '{operation}' not supported")
///         };
///     }
/// }
/// </code>
/// </remarks>
public interface IConnectorClient
{
    /// <summary>
    /// Gets the tool name that identifies this connector.
    /// Used for runtime lookup via <see cref="IConnectorClientFactory"/>
    /// and must match the tool name declared in SKILL.md files.
    /// </summary>
    /// <example>
    /// "azure_devops_work_items", "github_repos", "slack_notifications"
    /// </example>
    string ToolName { get; }

    /// <summary>
    /// Gets whether this connector is properly configured and available for use.
    /// Checks configuration (API keys, endpoints) without making external calls.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Gets the list of supported operations for this connector.
    /// </summary>
    /// <example>
    /// ["create_issue", "update_issue", "list_issues"]
    /// </example>
    IReadOnlyList<string> SupportedOperations { get; }

    /// <summary>
    /// Executes an operation with the given parameters.
    /// </summary>
    /// <param name="operation">Operation name (must be in <see cref="SupportedOperations"/>).</param>
    /// <param name="parameters">Operation parameters as key-value pairs.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Operation result with success status and data.</returns>
    Task<ConnectorOperationResult> ExecuteAsync(
        string operation,
        Dictionary<string, object> parameters,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates parameters for a specific operation without executing it.
    /// </summary>
    /// <param name="operation">Operation name.</param>
    /// <param name="parameters">Parameters to validate.</param>
    /// <returns>List of validation errors (empty if valid).</returns>
    Task<List<string>> ValidateParametersAsync(
        string operation,
        Dictionary<string, object> parameters);
}
