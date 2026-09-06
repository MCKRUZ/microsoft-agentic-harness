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
/// harness actually talks to that server. This validator does not connect to any server itself
/// (that would make host startup depend on every configured third-party server being reachable,
/// which the existing lazy MCP connection design deliberately avoids). Those entries are seeded
/// into <see cref="IPluginToolBoundaryTracker"/> here and resolved or fail-closed-faulted later,
/// lazily, by <see cref="IPluginToolBoundaryTracker.ReportServerToolsDiscovered"/> the first time
/// the harness organically discovers that server's tools (see that method's remarks).
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
    private readonly ILogger<PluginToolBoundaryStartupValidator> _logger;

    /// <summary>Initializes a new instance of the <see cref="PluginToolBoundaryStartupValidator"/> class.</summary>
    /// <param name="registry">Source of the loaded plugins to validate.</param>
    /// <param name="tracker">Seeded with every plugin's boundary entries.</param>
    /// <param name="isKnownFirstPartyToolName">
    /// Case-insensitive first-party (keyed-DI) tool-name membership check — see
    /// <see cref="IPluginToolBoundaryTracker.Seed"/>'s remarks for why case-insensitivity matters.
    /// </param>
    /// <param name="aiConfig">
    /// Supplies the enabled MCP server names configured anywhere on the host, read fresh inside
    /// <see cref="StartAsync"/> — see this type's remarks for why it cannot be resolved any earlier.
    /// </param>
    /// <param name="logger">Records the validated boundary shape.</param>
    public PluginToolBoundaryStartupValidator(
        IPluginRegistry registry,
        IPluginToolBoundaryTracker tracker,
        Func<string, bool> isKnownFirstPartyToolName,
        IOptionsMonitor<AIConfig> aiConfig,
        ILogger<PluginToolBoundaryStartupValidator> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(isKnownFirstPartyToolName);
        ArgumentNullException.ThrowIfNull(aiConfig);
        ArgumentNullException.ThrowIfNull(logger);

        _registry = registry;
        _tracker = tracker;
        _isKnownFirstPartyToolName = isKnownFirstPartyToolName;
        _aiConfig = aiConfig;
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
        var allConfiguredMcpServerNames = _aiConfig.CurrentValue.McpServers.Servers
            .Where(kvp => kvp.Value.Enabled)
            .Select(kvp => kvp.Key)
            .ToList();

        var immediateViolations = _tracker.Seed(loadedPlugins, _isKnownFirstPartyToolName, allConfiguredMcpServerNames);
        if (immediateViolations.Count == 0)
        {
            _logger.LogInformation(
                "Plugin tool boundaries validated: {PluginCount} loaded plugin(s), no immediately " +
                "unresolvable AllowedTools/DeniedTools entries.",
                loadedPlugins.Count);
            return Task.CompletedTask;
        }

        var lines = immediateViolations.Select(v =>
            $"Plugin '{v.PluginName}': {v.ListKind} entry '{v.ToolName}' matches no first-party tool, " +
            "and this plugin declares no MCP server that could ever supply it either.");

        throw new InvalidOperationException(
            "One or more plugin AllowedTools/DeniedTools entries name a tool that does not exist, so "
            + "the host refuses to boot (#524 — an unrecognized DeniedTools entry silently denies "
            + "nothing, which is worse than an error). Fix the following then restart:\n - "
            + string.Join("\n - ", lines));
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
