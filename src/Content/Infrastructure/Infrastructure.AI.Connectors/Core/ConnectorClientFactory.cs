using Application.AI.Common.Interfaces.Connectors;

namespace Infrastructure.AI.Connectors.Core;

/// <summary>
/// Default implementation of <see cref="IConnectorClientFactory"/>.
/// Caches the client collection at construction time since connectors are singletons.
/// </summary>
public sealed class ConnectorClientFactory : IConnectorClientFactory
{
    private readonly IReadOnlyList<IConnectorClient> _allClients;
    private readonly Dictionary<string, IConnectorClient> _clientsByName;

    /// <summary>
    /// Initializes a new instance of <see cref="ConnectorClientFactory"/>.
    /// </summary>
    /// <param name="clients">All registered connector clients from DI.</param>
    public ConnectorClientFactory(IEnumerable<IConnectorClient> clients)
    {
        _allClients = clients.ToList();
        _clientsByName = _allClients.ToDictionary(
            c => c.ToolName, c => c, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public IConnectorClient? GetClient(string toolName)
    {
        return _clientsByName.GetValueOrDefault(toolName);
    }

    /// <inheritdoc/>
    public IReadOnlyList<IConnectorClient> GetAllClients()
    {
        return _allClients;
    }

    /// <inheritdoc/>
    public IReadOnlyList<IConnectorClient> GetAvailableClients()
    {
        // Not cached — IsAvailable may change at runtime via IOptionsMonitor
        return _allClients.Where(c => c.IsAvailable).ToList();
    }
}
