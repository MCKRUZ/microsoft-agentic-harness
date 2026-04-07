using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Models;

namespace Application.AI.Common.Interfaces.Connectors;

/// <summary>
/// Bridges the connector system into the harness tool pipeline by adapting
/// an <see cref="IConnectorClient"/> to the <see cref="ITool"/> interface.
/// </summary>
/// <remarks>
/// <para>
/// The harness discovers and invokes tools via keyed DI as <c>ITool</c>.
/// Connectors use a richer interface (<c>IConnectorClient</c>) with availability
/// checking, parameter validation, and markdown output. This adapter bridges
/// the two so connectors are visible to the agent's orchestration loop.
/// </para>
/// <para>
/// <strong>Result translation:</strong> The adapter prefers <see cref="ConnectorOperationResult.MarkdownResult"/>
/// for LLM-friendly output. Falls back to <see cref="ConnectorOperationResult.Data"/> serialized
/// as a string, then a generic success message.
/// </para>
/// <para>
/// <strong>Registration:</strong>
/// <code>
/// // In DependencyInjection.cs:
/// services.AddKeyedSingleton&lt;ITool&gt;("github_issues",
///     (sp, _) =&gt; new ConnectorToolAdapter(
///         sp.GetRequiredService&lt;IEnumerable&lt;IConnectorClient&gt;&gt;()
///           .First(c =&gt; c.ToolName == "github_issues")));
/// </code>
/// </para>
/// </remarks>
public sealed class ConnectorToolAdapter : ITool
{
    private readonly IConnectorClient _connector;

    /// <summary>
    /// Initializes a new instance of <see cref="ConnectorToolAdapter"/>.
    /// </summary>
    /// <param name="connector">The connector client to adapt.</param>
    public ConnectorToolAdapter(IConnectorClient connector)
    {
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
    }

    /// <inheritdoc/>
    public string Name => _connector.ToolName;

    /// <inheritdoc/>
    public string Description => $"External connector: {_connector.ToolName}";

    /// <inheritdoc/>
    public IReadOnlyList<string> SupportedOperations => _connector.SupportedOperations;

    /// <inheritdoc/>
    public async Task<ToolResult> ExecuteAsync(
        string operation,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        // Translate IReadOnlyDictionary<string, object?> → Dictionary<string, object>
        var connectorParams = new Dictionary<string, object>();
        foreach (var kvp in parameters)
        {
            if (kvp.Value is not null)
                connectorParams[kvp.Key] = kvp.Value;
        }

        var result = await _connector.ExecuteAsync(operation, connectorParams, cancellationToken);

        if (!result.IsSuccess)
            return ToolResult.Fail(result.ErrorMessage ?? "Connector operation failed");

        // Prefer markdown (LLM-friendly), fall back to Data, then generic message
        var output = result.MarkdownResult
            ?? result.Data?.ToString()
            ?? "Operation completed successfully";

        return ToolResult.Ok(output);
    }
}
