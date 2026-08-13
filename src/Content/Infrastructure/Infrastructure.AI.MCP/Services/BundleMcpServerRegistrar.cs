using System.Linq;
using Application.AI.Common.Interfaces.Bundles;
using Domain.Common.Config.AI.MCP;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.MCP.Services;

/// <summary>
/// Default <see cref="IBundleMcpServerRegistrar"/>. Removes a bundle's namespaced entries from the
/// shared <see cref="BundleOwnedMcpServerRegistry"/> and disconnects any live
/// <see cref="McpConnectionManager"/> client for each — the deregistration counterpart to the
/// registration <c>BundleStagingService</c> performs at staging time, against the SAME
/// <see cref="BundleOwnedMcpServerRegistry"/> instance.
/// </summary>
public sealed class BundleMcpServerRegistrar : IBundleMcpServerRegistrar
{
    private readonly BundleOwnedMcpServerRegistry _bundleOwnedMcpServers;
    private readonly McpConnectionManager _connectionManager;
    private readonly ILogger<BundleMcpServerRegistrar> _logger;

    /// <summary>Initializes a new <see cref="BundleMcpServerRegistrar"/>.</summary>
    public BundleMcpServerRegistrar(
        BundleOwnedMcpServerRegistry bundleOwnedMcpServers,
        McpConnectionManager connectionManager,
        ILogger<BundleMcpServerRegistrar> logger)
    {
        ArgumentNullException.ThrowIfNull(bundleOwnedMcpServers);
        ArgumentNullException.ThrowIfNull(connectionManager);
        ArgumentNullException.ThrowIfNull(logger);

        _bundleOwnedMcpServers = bundleOwnedMcpServers;
        _connectionManager = connectionManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task DeregisterAsync(IReadOnlyList<string> serverNames)
    {
        // Independent per-server teardown (each disconnect is real network/process I/O against a
        // distinct entry) — fanned out with Task.WhenAll rather than paid cumulatively one server at a
        // time, mirroring BundleRunExecutor.WithBundleOwnedToolGrantsAsync's discovery fan-out for the
        // same reason. Each task catches its own failure, so one server's teardown error can never stop
        // another's from running or from being cleaned up from the registry.
        await Task.WhenAll(serverNames.Select(DeregisterOneAsync)).ConfigureAwait(false);
    }

    private async Task DeregisterOneAsync(string serverName)
    {
        _bundleOwnedMcpServers.TryRemove(serverName);

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
