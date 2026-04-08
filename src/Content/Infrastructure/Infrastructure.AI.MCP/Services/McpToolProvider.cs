using Application.AI.Common.Interfaces;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.MCP.Services;

/// <summary>
/// Implements <see cref="IMcpToolProvider"/> by managing connections to
/// configured MCP servers and converting their tools to <see cref="AITool"/>.
/// </summary>
/// <remarks>
/// <para>
/// Uses <see cref="McpConnectionManager"/> for connection lifecycle. Tools
/// are discovered lazily on first request. Unavailable servers are logged
/// and skipped rather than throwing — the agent operates with a reduced
/// tool surface.
/// </para>
/// </remarks>
public sealed class McpToolProvider : IMcpToolProvider
{
    private readonly ILogger<McpToolProvider> _logger;
    private readonly McpConnectionManager _connectionManager;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="McpToolProvider"/> class.
    /// </summary>
    public McpToolProvider(
        ILogger<McpToolProvider> logger,
        McpConnectionManager connectionManager)
    {
        _logger = logger;
        _connectionManager = connectionManager;
    }

    /// <inheritdoc />
    public async Task<IList<AITool>> GetToolsAsync(string serverName, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            var client = await _connectionManager.GetClientAsync(serverName, cancellationToken);
            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);

            _logger.LogDebug(
                "Retrieved {ToolCount} tools from MCP server '{ServerName}'",
                tools.Count, serverName);

            // McpClientTool implements AITool
            return tools.Cast<AITool>().ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to get tools from MCP server '{ServerName}' — skipping",
                serverName);
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, IList<AITool>>> GetAllToolsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var result = new Dictionary<string, IList<AITool>>();
        var serverNames = _connectionManager.GetConfiguredServerNames();

        var tasks = serverNames.Select(async name =>
        {
            var tools = await GetToolsAsync(name, cancellationToken);
            return (Name: name, Tools: tools);
        });

        var results = await Task.WhenAll(tasks);

        foreach (var (name, tools) in results)
        {
            if (tools.Count > 0)
                result[name] = tools;
        }

        _logger.LogInformation(
            "Discovered {TotalTools} tools from {ServerCount} MCP servers",
            result.Values.Sum(t => t.Count),
            result.Count);

        return result;
    }

    /// <inheritdoc />
    public async Task<bool> IsServerAvailableAsync(string serverName, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            await _connectionManager.GetClientAsync(serverName, cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Does not dispose <see cref="McpConnectionManager"/> — the DI container
    /// owns its lifetime since both are registered as singletons.
    /// </remarks>
    public void Dispose()
    {
        _disposed = true;
    }
}
