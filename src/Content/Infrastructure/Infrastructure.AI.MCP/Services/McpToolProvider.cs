using System.Diagnostics;
using System.Diagnostics.Metrics;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Plugins;
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
/// before, with no added attempt or timeout. <see cref="IsServerAvailableAsync"/> does NOT get this
/// recovery — it still calls <c>GetClientAsync</c> directly and reports a stale cached client as
/// available, exactly as before this fix; only <see cref="GetToolsAsync"/> and its callers
/// (<see cref="GetAllToolsAsync"/>, <see cref="GetToolByNameAsync"/>) route through the retry path.
/// </para>
/// <para>
/// <strong>Known limitation: this recovers tool discovery, not tool invocation.</strong> An
/// <see cref="AITool"/> this method returns is bound to the specific <c>McpClient</c> it was listed
/// from; once handed to the agent's tool chain, an actual tool <em>call</em> invokes that binding
/// directly and never routes back through this class, so a session that goes stale between discovery
/// and invocation still fails unrecovered on the call itself. See
/// <see cref="McpConnectionManager.DisconnectAsync"/>'s remarks for the client-lifetime race this
/// implies and the design that would close both gaps at once — tracked as a follow-up, not solved in
/// this class.
/// </para>
/// </remarks>
public sealed class McpToolProvider : IMcpToolProvider
{
    private readonly ILogger<McpToolProvider> _logger;
    private readonly McpConnectionManager _connectionManager;
    private readonly IPluginToolBoundaryTracker? _boundaryTracker;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="McpToolProvider"/> class.
    /// </summary>
    /// <param name="boundaryTracker">
    /// Optional (#524). When supplied, every successful tool-list discovery is reported to it so a
    /// plugin's AllowedTools/DeniedTools entries can be resolved or fail-closed-faulted against real
    /// MCP tool names — see <see cref="IPluginToolBoundaryTracker"/>'s remarks. Absent in most tests,
    /// which have nothing to report to; production DI always supplies it.
    /// </param>
    public McpToolProvider(
        ILogger<McpToolProvider> logger,
        McpConnectionManager connectionManager,
        IPluginToolBoundaryTracker? boundaryTracker = null)
    {
        _logger = logger;
        _connectionManager = connectionManager;
        _boundaryTracker = boundaryTracker;
    }

    /// <inheritdoc />
    public async Task<IList<AITool>> GetToolsAsync(string serverName, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var client = await TryConnectAsync(
            ct => _connectionManager.GetClientAsync(serverName, ct), serverName, "connect", cancellationToken);
        if (client is null)
            return [];

        try
        {
            return await DiscoverToolsAsync(client, serverName, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return [];
        }
        catch (Exception ex)
        {
            return await RetryAfterReconnectAsync(serverName, client, ex, cancellationToken);
        }
    }

    /// <summary>
    /// Obtains a client via <paramref name="connect"/>, returning <see langword="null"/> instead of
    /// throwing on failure — the shared "get a client or degrade" decision behind both the initial
    /// connect (<see cref="McpConnectionManager.GetClientAsync"/>) and the post-failure reconnect
    /// (<see cref="McpConnectionManager.ReconnectAsync"/>), which differ only in which connection-manager
    /// method they call and what to name in the log.
    /// </summary>
    private async Task<McpClient?> TryConnectAsync(
        Func<CancellationToken, Task<McpClient>> connect, string serverName, string action, CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            return await connect(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up, not the server — nothing failed here worth logging or metering.
            return null;
        }
        catch (Exception ex)
        {
            // Tagged Unavailable, not Error: this call never reached a live session at all, distinct
            // from a call that failed AFTER a client was successfully obtained (DiscoverToolsAsync's
            // Error tag) — the distinction GetToolsAsync's retry decision hinges on.
            RecordOutcome(start, serverName, McpConventions.StatusValues.Unavailable);
            _logger.LogWarning(ex, "Failed to {Action} to MCP server '{ServerName}' — skipping", action, serverName);
            return null;
        }
    }

    /// <summary>
    /// The connection existed — possibly cached from an earlier turn — but using it just failed. That
    /// is the shape a remote-restarted, server-evicted session takes (#385), not a server that is
    /// genuinely unreachable, so ask <see cref="McpConnectionManager"/> to evict and reconnect before
    /// falling back to "unavailable this turn". <see cref="McpConnectionManager.ReconnectAsync"/> owns
    /// the eviction, not this method — it is the only place that can tell whether a concurrent caller
    /// already recovered the same stale client. That includes a call that failed with
    /// <see cref="ObjectDisposedException"/> because a concurrent reconnect for this server disposed
    /// <paramref name="failedClient"/> out from under it: <see cref="McpConnectionManager.ReconnectAsync"/>
    /// only ever compares <paramref name="failedClient"/> by reference, never dereferences it, so calling
    /// it with an already-disposed reference is safe and simply returns the concurrent reconnect's fresh
    /// client instead of reconnecting a second time.
    /// </summary>
    /// <remarks>
    /// Reconnect and retry are two separate stages, each with its own outcome, rather than one
    /// enclosing <c>try</c>: <see cref="DiscoverToolsAsync"/> already records its own outcome (including
    /// <c>Error</c>, with real elapsed time) before rethrowing, so a single catch around both stages
    /// would record a second, misleading near-zero-duration <c>Error</c> for the SAME failed call. A
    /// caller's own cancellation degrades to <c>[]</c> with no log or metric — it isn't a server outcome
    /// — never rethrown, since this class's contract is "skipped rather than throwing" for every caller
    /// (<see cref="GetAllToolsAsync"/> fans this out via <c>Task.WhenAll</c> with no per-task guard;
    /// letting cancellation escape here would propagate out of that fan-out unhandled).
    /// </remarks>
    private async Task<IList<AITool>> RetryAfterReconnectAsync(
        string serverName, McpClient failedClient, Exception cause, CancellationToken cancellationToken)
    {
        _logger.LogInformation(cause,
            "MCP server '{ServerName}' rejected a call on its existing connection — reconnecting and retrying once",
            serverName);

        var freshClient = await TryConnectAsync(
            ct => _connectionManager.ReconnectAsync(serverName, failedClient, ct), serverName, "reconnect", cancellationToken);
        if (freshClient is null)
            return [];

        try
        {
            return await DiscoverToolsAsync(freshClient, serverName, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return [];
        }
        catch (Exception ex)
        {
            // DiscoverToolsAsync already recorded the Error outcome (with real elapsed time) before
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
    /// only the retry, never the failed attempt that preceded it. Named distinctly from
    /// <c>McpClient.ListToolsAsync</c>, which it wraps at line-of-call, to keep the two apart at
    /// a glance.
    /// </summary>
    private async Task<IList<AITool>> DiscoverToolsAsync(McpClient client, string serverName, CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
            RecordOutcome(start, serverName, McpConventions.StatusValues.Available);

            _logger.LogDebug(
                "Retrieved {ToolCount} tools from MCP server '{ServerName}'",
                tools.Count, serverName);

            ReportDiscoveryToBoundaryTracker(serverName, tools.Select(t => t.Name));

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

    /// <summary>
    /// Reports a server's just-discovered tool names to the plugin tool-boundary tracker (#524), so
    /// any plugin depending on this server can resolve or fail-closed-fault its
    /// AllowedTools/DeniedTools entries against them. A no-op when no tracker is wired (most tests)
    /// or nothing is pending for this server (the overwhelmingly common case). Logged at Critical,
    /// not thrown — the tracker never throws, and a boundary violation must not disturb this
    /// otherwise-successful discovery call's own return value; enforcement happens separately, via
    /// <c>IPluginRegistry.IsBoundaryFaulted</c> denying the plugin's tools on its next resolution.
    /// </summary>
    /// <remarks>
    /// Takes bare tool names rather than <see cref="McpClientTool"/> instances specifically so this
    /// is unit-testable without a live MCP connection — a real correctness-review finding on #524
    /// flagged this call site (the untrusted-input half of the feature) as having zero test coverage,
    /// since no fixture in this project exercises <see cref="DiscoverToolsAsync"/>'s success path.
    /// <c>internal</c> + <c>InternalsVisibleTo</c> lets <c>McpToolProviderTests</c> call this directly.
    /// </remarks>
    internal void ReportDiscoveryToBoundaryTracker(string serverName, IEnumerable<string> discoveredToolNames)
    {
        if (_boundaryTracker is null)
            return;

        var violations = _boundaryTracker.ReportServerToolsDiscovered(serverName, discoveredToolNames.ToList());
        foreach (var violation in violations)
        {
            _logger.LogCritical(
                "Plugin '{Plugin}': {ListKind} entry '{ToolName}' matches no known tool (first-party " +
                "or MCP) — this entry is a no-op, and the plugin's tool boundary can no longer be " +
                "trusted, so all tools from this plugin are now denied.",
                violation.PluginName, violation.ListKind, violation.ToolName);
        }
    }

    /// <summary>
    /// Records one outcome for the <c>list_tools</c> operation series. Deliberately shared across all
    /// three sub-stages this class can fail at — initial connect, reconnect, and the actual list call —
    /// rather than one series per sub-stage, so a dashboard sees one coherent "did GetToolsAsync work"
    /// signal with <see cref="McpConventions.StatusValues.Unavailable"/> distinguishing "never reached a
    /// session" from <see cref="McpConventions.StatusValues.Error"/> "reached one and it failed".
    /// </summary>
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
