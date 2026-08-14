using System.Diagnostics;
using System.Diagnostics.Metrics;
using Application.AI.Common.Interfaces;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Telemetry.Conventions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

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
/// <para>
/// <strong>A cached connection that a previous call already used is retried once, fresh, before
/// giving up.</strong> <see cref="McpConnectionManager"/> caches one client per server for the
/// process lifetime; if the remote restarts or evicts the session in between calls, the cached
/// client is stale but <em>looks</em> fine — <c>GetClientAsync</c> has no health check, so the
/// staleness surfaces only when a call actually reaches the remote and it rejects a session it no
/// longer recognises (#385). Recovery — evicting the stale client and connecting fresh — is owned by
/// <see cref="McpConnectionManager.ReconnectAsync"/>, not this class: it is the cache's job to decide
/// whether a reconnect another caller already performed makes this one redundant. Retrying is scoped
/// narrowly — only a failure that happens <em>after</em> a client was successfully obtained triggers
/// this path; a server that was never reachable in the first place still fails exactly as fast as
/// before, with no added attempt or timeout.
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

        McpClient client;
        try
        {
            client = await _connectionManager.GetClientAsync(serverName, cancellationToken);
        }
        catch (Exception ex)
        {
            // Never reachable at all this call — no cached connection existed to go stale, so
            // there is nothing a reconnect-and-retry would fix. Same behaviour as before this fix.
            RecordOutcome(Stopwatch.StartNew(), serverName, McpConventions.StatusValues.Unavailable);
            _logger.LogWarning(ex, "Failed to connect to MCP server '{ServerName}' — skipping", serverName);
            return [];
        }

        try
        {
            return await ListToolsAsync(client, serverName, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller's own token fired — not a signal the connection is stale. Let it propagate
            // rather than spending a reconnect attempt on a call nobody is waiting for.
            throw;
        }
        catch (Exception ex)
        {
            return await RetryAfterReconnectAsync(serverName, client, ex, cancellationToken);
        }
    }

    /// <summary>
    /// The connection existed — possibly cached from an earlier turn — but using it just failed. That
    /// is the shape a remote-restarted, server-evicted session takes (#385), not a server that is
    /// genuinely unreachable, so ask <see cref="McpConnectionManager"/> to evict and reconnect before
    /// falling back to "unavailable this turn". <see cref="McpConnectionManager.ReconnectAsync"/> owns
    /// the eviction, not this method — it is the only place that can tell whether a concurrent caller
    /// already recovered the same stale client, and returning here degrades to <c>[]</c> for any
    /// failure the reconnect attempt raises, honoring this class's "skipped rather than throwing"
    /// contract exactly as the first attempt does.
    /// </summary>
    private async Task<IList<AITool>> RetryAfterReconnectAsync(
        string serverName, McpClient failedClient, Exception cause, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation(cause,
                "MCP server '{ServerName}' rejected a call on its existing connection — reconnecting and retrying once",
                serverName);
            var freshClient = await _connectionManager.ReconnectAsync(serverName, failedClient, cancellationToken);
            return await ListToolsAsync(freshClient, serverName, cancellationToken);
        }
        catch (Exception retryEx)
        {
            RecordOutcome(Stopwatch.StartNew(), serverName, McpConventions.StatusValues.Error);
            _logger.LogWarning(retryEx,
                "Failed to get tools from MCP server '{ServerName}' after reconnecting — skipping",
                serverName);
            return [];
        }
    }

    /// <summary>
    /// Lists tools on an already-obtained client and projects them to <see cref="AITool"/>. Records
    /// the outcome metric itself — for both success and failure — so a retried call's timing reflects
    /// only the retry, never the failed attempt that preceded it.
    /// </summary>
    private async Task<IList<AITool>> ListToolsAsync(McpClient client, string serverName, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
            RecordOutcome(sw, serverName, McpConventions.StatusValues.Available);

            _logger.LogDebug(
                "Retrieved {ToolCount} tools from MCP server '{ServerName}'",
                tools.Count, serverName);

            // McpClientTool implements AITool
            return tools.Cast<AITool>().ToList();
        }
        catch (Exception)
        {
            RecordOutcome(sw, serverName, McpConventions.StatusValues.Error);
            throw;
        }
    }

    private static void RecordOutcome(Stopwatch sw, string serverName, string status)
    {
        sw.Stop();
        var tags = new TagList
        {
            { McpConventions.ServerName, serverName },
            { McpConventions.Operation, "list_tools" },
            { McpConventions.Status, status }
        };
        McpServerMetrics.RequestDuration.Record(sw.Elapsed.TotalMilliseconds, tags);
        McpServerMetrics.Requests.Add(1, tags);
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
    public async Task<AIFunction?> GetToolByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        foreach (var serverName in _connectionManager.GetConfiguredServerNames())
        {
            var tools = await GetToolsAsync(serverName, cancellationToken);
            var match = tools.OfType<AIFunction>().FirstOrDefault(fn => fn.Name == name);
            if (match is not null)
                return match;
        }
        return null;
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
