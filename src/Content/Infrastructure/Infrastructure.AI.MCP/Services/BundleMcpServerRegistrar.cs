using Application.AI.Common.Interfaces.Bundles;
using Domain.Common.Config.AI.MCP;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.MCP.Services;

/// <summary>
/// Default <see cref="IBundleMcpServerRegistrar"/>. Removes a bundle's namespaced entries from the
/// shared <see cref="McpServersConfig"/> and disconnects any live <see cref="McpConnectionManager"/>
/// client for each — the deregistration counterpart to the registration <c>BundleStagingService</c>
/// performs at staging time, against the SAME <see cref="McpServersConfig"/> instance.
/// </summary>
public sealed class BundleMcpServerRegistrar : IBundleMcpServerRegistrar
{
    private readonly McpServersConfig _mcpServersConfig;
    private readonly McpConnectionManager _connectionManager;
    private readonly ILogger<BundleMcpServerRegistrar> _logger;

    /// <summary>Initializes a new <see cref="BundleMcpServerRegistrar"/>.</summary>
    public BundleMcpServerRegistrar(
        McpServersConfig mcpServersConfig,
        McpConnectionManager connectionManager,
        ILogger<BundleMcpServerRegistrar> logger)
    {
        ArgumentNullException.ThrowIfNull(mcpServersConfig);
        ArgumentNullException.ThrowIfNull(connectionManager);
        ArgumentNullException.ThrowIfNull(logger);

        _mcpServersConfig = mcpServersConfig;
        _connectionManager = connectionManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task DeregisterAsync(IReadOnlyList<string> serverNames)
    {
        foreach (var serverName in serverNames)
        {
            _mcpServersConfig.Servers.TryRemove(serverName, out _);

            try
            {
                // Idempotent for a name with no active client (McpConnectionManager.DisconnectAsync is a
                // no-op TryRemove) — safe to call even if this bundle's server was never actually
                // contacted during its run.
                await _connectionManager.DisconnectAsync(serverName).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to disconnect MCP client for deregistered bundle server '{Server}'; " +
                    "the connection will be cleaned up on host shutdown",
                    serverName);
            }
        }
    }
}
