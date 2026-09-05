using Application.AI.Common.Interfaces.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Plugins;

/// <summary>
/// One-shot startup check for #524: a loaded plugin's <c>AllowedTools</c>/<c>DeniedTools</c> entry
/// that provably matches no tool at all — because the plugin declares zero MCP servers, so a
/// first-party (keyed-DI) name is the only kind of name it could ever resolve to — refuses to boot.
/// </summary>
/// <remarks>
/// <para>
/// This is only the immediately-decidable half of #524. A plugin that DOES declare MCP servers may
/// have entries that are real MCP tool names, only knowable once the harness actually talks to that
/// server — this validator does not connect to any server itself (that would make host startup
/// depend on every configured third-party server being reachable, which the existing lazy MCP
/// connection design deliberately avoids). Those entries are seeded into
/// <see cref="IPluginToolBoundaryTracker"/> here and resolved or fail-closed-faulted later, lazily,
/// by <see cref="IPluginToolBoundaryTracker.ReportServerToolsDiscovered"/> the first time the
/// harness organically discovers that server's tools (see that method's remarks).
/// </para>
/// <para>
/// Registered as <see cref="IHostedService"/>, matching <c>ToolAuthorizationConfigValidator</c>'s
/// shape rather than <c>AbstractValidator&lt;T&gt;</c> — this needs the live keyed-DI tool-name set,
/// which a parameterless-constructor FluentValidation validator cannot take as a dependency. Must be
/// registered <em>after</em> <c>PluginStartupLoader</c> (see the DI registration) so
/// <see cref="IPluginRegistry"/> is already populated when <see cref="StartAsync"/> runs.
/// </para>
/// </remarks>
public sealed class PluginToolBoundaryStartupValidator : IHostedService
{
    private readonly IPluginRegistry _registry;
    private readonly IPluginToolBoundaryTracker _tracker;
    private readonly Func<string, bool> _isKnownFirstPartyToolName;
    private readonly ILogger<PluginToolBoundaryStartupValidator> _logger;

    /// <summary>Initializes a new instance of the <see cref="PluginToolBoundaryStartupValidator"/> class.</summary>
    /// <param name="registry">Source of the loaded plugins to validate.</param>
    /// <param name="tracker">Seeded with every plugin's boundary entries.</param>
    /// <param name="isKnownFirstPartyToolName">
    /// Case-insensitive first-party (keyed-DI) tool-name membership check — see
    /// <see cref="IPluginToolBoundaryTracker.Seed"/>'s remarks for why case-insensitivity matters.
    /// </param>
    /// <param name="logger">Records the validated boundary shape.</param>
    public PluginToolBoundaryStartupValidator(
        IPluginRegistry registry,
        IPluginToolBoundaryTracker tracker,
        Func<string, bool> isKnownFirstPartyToolName,
        ILogger<PluginToolBoundaryStartupValidator> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(isKnownFirstPartyToolName);
        ArgumentNullException.ThrowIfNull(logger);

        _registry = registry;
        _tracker = tracker;
        _isKnownFirstPartyToolName = isKnownFirstPartyToolName;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var loadedPlugins = _registry.GetLoadedPlugins()
            .Where(p => p.Status == PluginLoadStatus.Loaded)
            .ToList();

        var immediateViolations = _tracker.Seed(loadedPlugins, _isKnownFirstPartyToolName);
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
