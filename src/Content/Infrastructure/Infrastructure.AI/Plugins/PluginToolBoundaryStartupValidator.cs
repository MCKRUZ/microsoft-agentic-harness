using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Plugins;
using Domain.Common.Config.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Plugins;

/// <summary>
/// One-shot startup check for #524: a loaded plugin's <c>AllowedTools</c>/<c>DeniedTools</c> entry
/// that provably matches no tool at all — no MCP server is configured anywhere on the host, so a
/// first-party (keyed-DI) name is the only kind of name it could ever resolve to — refuses to boot.
/// </summary>
/// <remarks>
/// <para>
/// This is only the immediately-decidable half of #524. Whenever at least one MCP server is
/// configured on the host, an entry might be a real MCP tool name — a plugin skill's tool
/// declaration can resolve against ANY host-configured server, not just ones the plugin itself
/// declares (see <c>ToolChainBuilder.ResolveEffectiveMcpServerName</c>) — only knowable once the
/// harness actually talks to that server. Those entries are seeded into
/// <see cref="IPluginToolBoundaryTracker"/> as <see cref="PluginBoundaryStatus.Pending"/> here, and
/// resolved or fail-closed-faulted by <see cref="IPluginToolBoundaryTracker.ReportServerToolsDiscovered"/>
/// whenever the harness discovers that server's tools — including a proactive, fire-and-forget
/// query this validator itself kicks off for every <see cref="IPluginToolBoundaryTracker.PendingServerNames"/>
/// server right after <see cref="StartAsync"/> seeds them, not only when the running session
/// organically needs that server. <strong>Host boot itself still never blocks on this</strong> — the
/// proactive query is started, never awaited, so a slow or unreachable third-party server delays how
/// quickly its dependent plugins leave <see cref="PluginBoundaryStatus.Pending"/>, never delays
/// <see cref="StartAsync"/> returning. A plugin whose boundary is still resolving when a request
/// arrives is denied, not trusted — see <see cref="PluginBoundaryStatus"/>'s remarks for why that is
/// the deliberate fail-closed default, not a race condition to route around.
/// </para>
/// <para>
/// Registered as <see cref="IHostedService"/>, matching <c>ToolAuthorizationConfigValidator</c>'s
/// shape rather than <c>AbstractValidator&lt;T&gt;</c> — this needs the live keyed-DI tool-name set,
/// which a parameterless-constructor FluentValidation validator cannot take as a dependency. Must be
/// registered <em>after</em> <c>PluginStartupLoader</c> (see the DI registration) so
/// <see cref="IPluginRegistry"/> is already populated when <see cref="StartAsync"/> runs.
/// </para>
/// <para>
/// <strong>The MCP server list is read from <see cref="IOptionsMonitor{TOptions}"/> inside
/// <see cref="StartAsync"/>, never captured at construction time.</strong> The .NET Generic Host
/// resolves — and so constructs — every <see cref="IHostedService"/> up front, before calling
/// <c>StartAsync</c> on any of them; <c>PluginStartupLoader</c> only merges a plugin's own MCP
/// servers into the shared <see cref="AIConfig.McpServers"/> instance from inside its own
/// <c>StartAsync</c>. Reading the server list at construction (e.g. captured once by the DI factory)
/// would therefore see the config from BEFORE that merge — missing every plugin-declared server —
/// regardless of registration order. Reading it fresh inside this type's own <c>StartAsync</c>,
/// which the Host guarantees runs after <c>PluginStartupLoader.StartAsync</c> completes, is what
/// actually sees the merged list.
/// </para>
/// </remarks>
public sealed class PluginToolBoundaryStartupValidator : IHostedService
{
    private readonly IPluginRegistry _registry;
    private readonly IPluginToolBoundaryTracker _tracker;
    private readonly Func<string, bool> _isKnownFirstPartyToolName;
    private readonly IOptionsMonitor<AIConfig> _aiConfig;
    private readonly IMcpToolProvider _toolProvider;
    private readonly ILogger<PluginToolBoundaryStartupValidator> _logger;

    /// <summary>Initializes a new instance of the <see cref="PluginToolBoundaryStartupValidator"/> class.</summary>
    /// <param name="registry">Source of the loaded plugins to validate.</param>
    /// <param name="tracker">Seeded with every plugin's boundary entries.</param>
    /// <param name="isKnownFirstPartyToolName">
    /// Case-insensitive first-party (keyed-DI) tool-name membership check — see
    /// <see cref="IPluginToolBoundaryTracker.Seed"/>'s remarks for why case-insensitivity matters.
    /// </param>
    /// <param name="aiConfig">
    /// Supplies every MCP server configured anywhere on the host — enabled and disabled alike (#613) —
    /// read fresh inside <see cref="StartAsync"/>, which separates them into
    /// <see cref="IPluginToolBoundaryTracker.Seed"/>'s existence-check list (all of them) and the
    /// background prober's query list (enabled only). See this type's remarks for why config cannot be
    /// resolved any earlier than <see cref="StartAsync"/>.
    /// </param>
    /// <param name="toolProvider">
    /// Used to proactively resolve every <see cref="IPluginToolBoundaryTracker.PendingServerNames"/>
    /// server right after <see cref="IPluginToolBoundaryTracker.Seed"/> — fire-and-forget, in the
    /// background, never awaited before <see cref="StartAsync"/> returns — so a plugin boundary
    /// doesn't stay <see cref="PluginBoundaryStatus.Pending"/> (and therefore denied) for the rest of
    /// the process lifetime just because nothing else happens to query that server. See this type's
    /// remarks for why boot itself must still never block on live third-party connectivity.
    /// </param>
    /// <param name="logger">Records the validated boundary shape.</param>
    public PluginToolBoundaryStartupValidator(
        IPluginRegistry registry,
        IPluginToolBoundaryTracker tracker,
        Func<string, bool> isKnownFirstPartyToolName,
        IOptionsMonitor<AIConfig> aiConfig,
        IMcpToolProvider toolProvider,
        ILogger<PluginToolBoundaryStartupValidator> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(isKnownFirstPartyToolName);
        ArgumentNullException.ThrowIfNull(aiConfig);
        ArgumentNullException.ThrowIfNull(toolProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _registry = registry;
        _tracker = tracker;
        _isKnownFirstPartyToolName = isKnownFirstPartyToolName;
        _aiConfig = aiConfig;
        _toolProvider = toolProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var loadedPlugins = _registry.GetLoadedPlugins()
            .Where(p => p.Status == PluginLoadStatus.Loaded)
            .ToList();

        // Read fresh here, not captured earlier — see this type's remarks for why. By this point
        // PluginStartupLoader.StartAsync has already merged every plugin's own MCP servers into the
        // SAME McpServersConfig.Servers instance (registration order = StartAsync order).
        //
        // #613: deliberately NOT filtered to Enabled — a disabled server is still a real, configured
        // server that could explain a boundary entry the moment it's re-enabled, so Seed's existence
        // check ("no MCP server is configured anywhere on this host") must see it too, or a plugin
        // legitimately referencing a merely-disabled server gets refused at boot. A plugin whose entry
        // is explained ONLY by a disabled server still ends up denied either way (Pending is fail-closed,
        // same as Faulted) — what this changes is honesty, not enforcement: Faulted is a provably-fake,
        // terminal, loudly-logged (Critical) verdict; Pending stays "still unresolved, awaiting real
        // information" — never definitively wrong, and not terminal by construction, though this
        // process still only ever seeds once (StartAsync), so an operator re-enabling the server
        // without restarting resolves it only if something else independently queries that server name
        // afterward (organic use, or a future restart re-running Seed), not automatically. The
        // background prober below still only ever queries the ENABLED subset — see
        // ResolvePendingServersInBackground.
        var allConfiguredMcpServerNames = _aiConfig.CurrentValue.McpServers.Servers
            .Select(kvp => kvp.Key)
            .ToList();
        var enabledMcpServerNames = new HashSet<string>(
            _aiConfig.CurrentValue.McpServers.Servers.Where(kvp => kvp.Value.Enabled).Select(kvp => kvp.Key),
            StringComparer.OrdinalIgnoreCase);

        var immediateViolations = _tracker.Seed(loadedPlugins, _isKnownFirstPartyToolName, allConfiguredMcpServerNames);
        if (immediateViolations.Count == 0)
        {
            _logger.LogInformation(
                "Plugin tool boundaries validated: {PluginCount} loaded plugin(s), no immediately " +
                "unresolvable AllowedTools/DeniedTools entries.",
                loadedPlugins.Count);

            ResolvePendingServersInBackground(enabledMcpServerNames);
            return Task.CompletedTask;
        }

        // #524 round-2 code-review: this branch fires when NO MCP server is configured anywhere on
        // the host (see Seed's own remarks) — not when this specific plugin declares none of its own.
        // #613: "configured" genuinely means present in config now, enabled or not — a disabled
        // server no longer collapses this to the zero-server case, so this message is now accurate
        // exactly as worded: it fires only when literally nothing is configured, period.
        var lines = immediateViolations.Select(v =>
            $"Plugin '{v.PluginName}': {v.ListKind} entry '{v.ToolName}' matches no first-party tool, " +
            "and no MCP server is configured anywhere on this host that could ever supply it either.");

        throw new InvalidOperationException(
            "One or more plugin AllowedTools/DeniedTools entries name a tool that does not exist, so "
            + "the host refuses to boot (#524 — an unrecognized DeniedTools entry silently denies "
            + "nothing, which is worse than an error). Fix the following then restart:\n - "
            + string.Join("\n - ", lines));
    }

    /// <summary>
    /// Fire-and-forget: queries every ENABLED <see cref="IPluginToolBoundaryTracker.PendingServerNames"/>
    /// server once, in the background, so a plugin boundary depending on it resolves promptly instead
    /// of only when the running session organically needs that server — see this type's remarks.
    /// Never awaited by <see cref="StartAsync"/>; each server's task owns its own exception handling
    /// so a connection failure here can neither propagate to an unobserved-task-exception handler nor
    /// block any other server's resolution.
    /// </summary>
    /// <param name="enabledServerNames">
    /// #613: <see cref="IPluginToolBoundaryTracker.PendingServerNames"/> can now legitimately include
    /// a currently-disabled server (see <see cref="StartAsync"/>'s remarks). A disabled server can
    /// never actually connect — <c>McpConnectionManager.CreateClientAsync</c> throws deterministically
    /// — so probing one here would only waste this method's retry budget and log noise for an outcome
    /// already known before trying; <see cref="IMcpToolProvider.GetToolsAsync"/> also independently
    /// refuses to report a discovery outcome for one (defense in depth), but skipping it here avoids
    /// the wasted attempt in the first place. Typed <see cref="HashSet{T}"/>, not a bare
    /// <see cref="IReadOnlyCollection{T}"/> (#613 security review): server names are matched
    /// case-insensitively everywhere else in this feature, and only a concrete
    /// <see cref="HashSet{T}"/> built with <see cref="StringComparer.OrdinalIgnoreCase"/> guarantees
    /// <c>.Contains</c> below honors that — an interface-typed parameter would still work today (the
    /// LINQ <c>Contains</c> extension's <c>ICollection&lt;T&gt;</c> fast path happens to reach the same
    /// set), but only incidentally, and would silently go ordinal-case-sensitive if a future caller
    /// passed a plain list or array instead.
    /// </param>
    private void ResolvePendingServersInBackground(HashSet<string> enabledServerNames)
    {
        foreach (var serverName in _tracker.PendingServerNames.Where(enabledServerNames.Contains))
        {
            _ = ResolveOneServerAsync(serverName);
        }
    }

    // A Stdio/spawned-process MCP server (npx, a container) can genuinely take a few seconds to
    // become reachable — this is not a failure, just a cold start still in progress.
    private const int MaxAvailabilityAttempts = 5;
    private static readonly TimeSpan AvailabilityRetryDelay = TimeSpan.FromSeconds(1);

    private async Task ResolveOneServerAsync(string serverName)
    {
        try
        {
            // #524 round-2 code-review: GetToolsAsync's failure path reports to the boundary tracker,
            // and ReportServerToolsDiscovered only honors the FIRST report per server — so a proactive
            // probe that races a still-starting server and loses would itself permanently fault the
            // plugin, with no way back short of a process restart. IsServerAvailableAsync has no such
            // side effect (confirmed: it never calls the tracker), so it's safe to retry here — only
            // the ONE real, reporting attempt below happens after the server is confirmed reachable or
            // this budget is exhausted, matching organic usage's own single-attempt behavior rather
            // than manufacturing a second, artificially-lenient reporting path.
            for (var attempt = 0; attempt < MaxAvailabilityAttempts; attempt++)
            {
                if (await _toolProvider.IsServerAvailableAsync(serverName))
                    break;
                if (attempt < MaxAvailabilityAttempts - 1)
                    await Task.Delay(AvailabilityRetryDelay);
            }

            // The result itself is discarded — GetToolsAsync's own success/failure path already
            // reports to the boundary tracker (McpToolProvider.DiscoverToolsAsync and its
            // connection-failure branch); this call exists purely to trigger that reporting sooner
            // than "whenever a skill happens to need this server" would.
            await _toolProvider.GetToolsAsync(serverName);
        }
        catch (Exception ex)
        {
            // GetToolsAsync's own contract is "skipped rather than throwing" for every caller it
            // documents (see its remarks) — this catch exists as a last-resort backstop against that
            // contract changing under this call site in the future, not because it is expected to
            // fire today.
            _logger.LogWarning(ex,
                "Proactive plugin-boundary resolution for MCP server '{ServerName}' failed unexpectedly",
                serverName);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
