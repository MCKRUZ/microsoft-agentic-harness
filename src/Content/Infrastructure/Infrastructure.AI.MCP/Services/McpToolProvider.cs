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
/// <para>
/// <strong>Known limitation: this recovers tool discovery, not tool invocation, and reconnect can now
/// race an in-flight call.</strong> An <see cref="AITool"/> this method returns is bound to the
/// specific <c>McpClient</c> it was listed from; once handed to the agent's tool chain, an actual tool
/// <em>call</em> invokes that binding directly and never routes back through this class. The exposure
/// varies by caller: <c>Presentation.AgentHub/Controllers/McpController.InvokeTool</c> calls
/// <see cref="GetToolByNameAsync"/> — and so gets this fix's recovery — immediately before invoking,
/// leaving only the brief window between that lookup and the call itself; the agent-turn pipeline
/// (<c>Application.Core/CQRS/Agents/ExecuteAgentTurn</c>) resolves tools once per turn and can hold that
/// binding across a materially longer window before invoking it, with no re-lookup in between. Either
/// way, a session that goes stale in that window still fails unrecovered on the call itself — only the
/// next discovery pass observes and repairs it. Because reconnect now happens automatically on any
/// discovery failure rather than only on an explicit admin disconnect, a call already in flight against
/// a client another caller's reconnect is about to evict can observe
/// <see cref="ObjectDisposedException"/> mid-invocation more often than before this fix. Closing this
/// needs a retry-aware wrapper around every <see cref="AITool"/> this method returns plus
/// reference-counted or generation-tagged client leases in <see cref="McpConnectionManager"/> — a
/// materially larger change than this fix; tracked as a follow-up, not solved in this class.
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

        var connectStart = Stopwatch.GetTimestamp();
        McpClient client;
        try
        {
            client = await _connectionManager.GetClientAsync(serverName, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up, not the server — nothing failed here worth logging or metering.
            return [];
        }
        catch (Exception ex)
        {
            // Never reachable at all this call — no cached connection existed to go stale, so there is
            // nothing a reconnect-and-retry would fix; still degrades to [] as before this fix, but now
            // tagged Unavailable rather than Error, distinguishing "never reachable" from "a call on an
            // established connection failed" — the two StatusValues this fix's retry decision hinges on.
            RecordOutcome(connectStart, serverName, McpConventions.StatusValues.Unavailable);
            _logger.LogWarning(ex, "Failed to connect to MCP server '{ServerName}' — skipping", serverName);
            return [];
        }

        try
        {
            return await ListToolsAsync(client, serverName, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return [];
        }
        catch (ObjectDisposedException)
        {
            // The client itself is already gone — most likely a concurrent reconnect for this server
            // (McpConnectionManager.ReconnectAsync's collapse behaviour) or an explicit DisconnectAsync
            // beat this call to eviction. Reconnecting from an already-disposed reference fixes nothing
            // this call could observe; ListToolsAsync already recorded the failure. Skip the wasted
            // round trip rather than retrying — the caller that evicted it is the one making progress.
            return [];
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
    /// already recovered the same stale client.
    /// </summary>
    /// <remarks>
    /// Reconnect and retry are two separate stages, each with its own outcome, rather than one
    /// enclosing <c>try</c>: <see cref="ListToolsAsync"/> already records its own outcome (including
    /// <c>Error</c>, with real elapsed time) before rethrowing, so a single catch around both stages
    /// would record a second, misleading near-zero-duration <c>Error</c> for the SAME failed call. A
    /// caller's own cancellation is caught in both stages and degrades to <c>[]</c> with no log or
    /// metric — it isn't a server outcome — never rethrown, since this class's contract is "skipped
    /// rather than throwing" for every caller (<see cref="GetAllToolsAsync"/> fans this out via
    /// <c>Task.WhenAll</c> with no per-task guard; letting cancellation escape here would propagate out
    /// of that fan-out unhandled).
    /// </remarks>
    private async Task<IList<AITool>> RetryAfterReconnectAsync(
        string serverName, McpClient failedClient, Exception cause, CancellationToken cancellationToken)
    {
        _logger.LogInformation(cause,
            "MCP server '{ServerName}' rejected a call on its existing connection — reconnecting and retrying once",
            serverName);

        var reconnectStart = Stopwatch.GetTimestamp();
        McpClient freshClient;
        try
        {
            freshClient = await _connectionManager.ReconnectAsync(serverName, failedClient, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return [];
        }
        catch (Exception ex)
        {
            RecordOutcome(reconnectStart, serverName, McpConventions.StatusValues.Unavailable);
            _logger.LogWarning(ex, "Failed to reconnect to MCP server '{ServerName}' — skipping", serverName);
            return [];
        }

        try
        {
            return await ListToolsAsync(freshClient, serverName, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return [];
        }
        catch (Exception ex)
        {
            // ListToolsAsync already recorded the Error outcome (with real elapsed time) before
            // rethrowing — do not record it again here.
            _logger.LogWarning(ex,
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
        var start = Stopwatch.GetTimestamp();
        try
        {
            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
            RecordOutcome(start, serverName, McpConventions.StatusValues.Available);

            _logger.LogDebug(
                "Retrieved {ToolCount} tools from MCP server '{ServerName}'",
                tools.Count, serverName);

            // McpClientTool implements AITool
            return tools.Cast<AITool>().ToList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up — not a server-side outcome, so no metric for it. Let the caller decide.
            throw;
        }
        catch (Exception)
        {
            RecordOutcome(start, serverName, McpConventions.StatusValues.Error);
            throw;
        }
    }

    private static void RecordOutcome(long startTimestamp, string serverName, string status)
    {
        var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
        var tags = new TagList
        {
            { McpConventions.ServerName, serverName },
            { McpConventions.Operation, "list_tools" },
            { McpConventions.Status, status }
        };
        McpServerMetrics.RequestDuration.Record(elapsed.TotalMilliseconds, tags);
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
