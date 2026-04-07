namespace Application.AI.Common.Interfaces.Connectors;

/// <summary>
/// Factory for retrieving connector clients by tool name at runtime.
/// Used by the agent harness to dispatch tool calls to the appropriate connector.
/// </summary>
public interface IConnectorClientFactory
{
    /// <summary>
    /// Gets a connector client by its tool name.
    /// Returns null if no connector is registered with that name.
    /// </summary>
    /// <param name="toolName">The tool name (e.g., "github_issues").</param>
    IConnectorClient? GetClient(string toolName);

    /// <summary>
    /// Gets all registered connector clients (regardless of availability).
    /// </summary>
    IReadOnlyList<IConnectorClient> GetAllClients();

    /// <summary>
    /// Gets only connector clients that are configured and available for use.
    /// </summary>
    IReadOnlyList<IConnectorClient> GetAvailableClients();
}
