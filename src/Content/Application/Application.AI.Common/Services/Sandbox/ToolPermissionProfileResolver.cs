using Application.AI.Common.Interfaces.Tools;
using Domain.Common.Helpers;
using Domain.AI.Sandbox;
using Domain.Common.Config.AI.Sandbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.Services.Sandbox;

/// <summary>
/// Resolves the effective <see cref="ToolPermissionProfile"/> for a tool by merging its
/// <see cref="ITool.RequiredCapabilities"/>/<see cref="ITool.MinimumIsolation"/> declaration with
/// runtime <see cref="ToolOverrideConfig"/> from appsettings. Uses deny-overrides-allow semantics.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <c>ToolCapabilityResolver</c> (the sibling resolver for the tool-composition capability
/// model): the base classification comes from a bounded-key-set-gated keyed-DI lookup of
/// <see cref="ITool"/>, not from a separately-populated cache a caller has to remember to feed. The
/// previous design read a <c>[ToolCapabilityAttribute]</c> cached via an explicit
/// <c>RegisterToolType</c> call — nothing in production ever called it, so every tool resolved
/// <see cref="ToolCapability.None"/> regardless of what it actually needed, and the capability
/// check downstream (<c>CapabilityEnforcer</c>) could never refuse a call (#387).
/// </para>
/// <para>
/// <strong>Never probes keyed DI with a name outside the bounded registered-key set supplied to the
/// constructor</strong> — see <c>ToolCapabilityResolver</c>'s remarks for why: <c>GetKeyedService</c>
/// caches an accessor per distinct key it is asked about, even for a key nothing is registered
/// under, in the ROOT container this singleton holds, so probing an unbounded name space (MCP or
/// bundle-owned tool names) would be unbounded, process-lifetime memory growth.
/// </para>
/// </remarks>
public sealed class ToolPermissionProfileResolver
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IOptionsMonitor<SandboxConfig> _config;
    private readonly IReadOnlySet<string> _registeredFirstPartyToolKeys;

    /// <summary>Initializes a new instance of the <see cref="ToolPermissionProfileResolver"/> class.</summary>
    /// <param name="serviceProvider">Root service provider, for bounded keyed-DI lookup of first-party tools.</param>
    /// <param name="config">Sandbox configuration with per-tool overrides.</param>
    /// <param name="registeredFirstPartyToolKeys">
    /// The bounded set of keys <see cref="ITool"/> is actually registered under — see this type's
    /// remarks for why probing keyed DI outside this set is unsafe.
    /// </param>
    public ToolPermissionProfileResolver(
        IServiceProvider serviceProvider,
        IOptionsMonitor<SandboxConfig> config,
        IReadOnlySet<string> registeredFirstPartyToolKeys)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(registeredFirstPartyToolKeys);

        _serviceProvider = serviceProvider;
        _config = config;
        _registeredFirstPartyToolKeys = registeredFirstPartyToolKeys;
    }

    /// <summary>
    /// Resolves the effective permission profile by merging the tool's own declaration
    /// (<see cref="ITool.RequiredCapabilities"/>/<see cref="ITool.MinimumIsolation"/>) with runtime
    /// configuration overrides.
    /// </summary>
    /// <param name="toolName">The keyed DI tool name.</param>
    /// <returns>The merged permission profile.</returns>
    public ToolPermissionProfile Resolve(string toolName)
    {
        var (baseCapabilities, baseIsolation) = ResolveBase(toolName);

        _config.CurrentValue.ToolOverrides.TryGetValue(toolName, out var overrideConfig);

        if (overrideConfig is null)
        {
            return new ToolPermissionProfile
            {
                RequiredCapabilities = baseCapabilities,
                MinimumIsolation = baseIsolation
            };
        }

        var deniedCaps = ParseCapabilities(overrideConfig.DeniedCapabilities);
        var effectiveCapabilities = baseCapabilities & ~deniedCaps;

        var overrideIsolation = EnumNameHelper.TryParseName<SandboxIsolationLevel>(
            overrideConfig.MinimumIsolation, out var parsed)
            ? parsed
            : SandboxIsolationLevel.None;
        var effectiveIsolation = (SandboxIsolationLevel)Math.Max(
            (int)baseIsolation, (int)overrideIsolation);

        return new ToolPermissionProfile
        {
            RequiredCapabilities = effectiveCapabilities,
            AllowedPaths = overrideConfig.AllowedPaths.AsReadOnly(),
            DeniedPaths = overrideConfig.DeniedPaths.AsReadOnly(),
            AllowedHosts = overrideConfig.AllowedHosts.AsReadOnly(),
            DeniedHosts = overrideConfig.DeniedHosts.AsReadOnly(),
            MinimumIsolation = effectiveIsolation
        };
    }

    /// <summary>
    /// The base declaration before any override: a registered first-party tool's own
    /// <see cref="ITool.RequiredCapabilities"/>/<see cref="ITool.MinimumIsolation"/>, or
    /// <see cref="ToolCapability.None"/>/<see cref="SandboxIsolationLevel.None"/> for a name outside
    /// the bounded registered-key set (MCP or bundle-owned tools — never covered by capability
    /// declarations either way).
    /// </summary>
    private (ToolCapability Capabilities, SandboxIsolationLevel Isolation) ResolveBase(string toolName)
    {
        var firstParty = _registeredFirstPartyToolKeys.Contains(toolName)
            ? _serviceProvider.GetKeyedService<ITool>(toolName)
            : null;

        return firstParty is not null
            ? (firstParty.RequiredCapabilities, firstParty.MinimumIsolation)
            : (ToolCapability.None, SandboxIsolationLevel.None);
    }

    /// <summary>
    /// Parses capability names (e.g., "FileRead", "NetworkAccess") into a combined
    /// <see cref="ToolCapability"/> flags value. Unrecognised names are ignored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Names only — but a comma-separated entry is still a list of names.</strong> Numeric
    /// forms are refused: <c>Enum.TryParse&lt;ToolCapability&gt;("255", …)</c> succeeds and sets
    /// every bit including undefined ones, which on the granting side
    /// (<c>SandboxConfig.DefaultGrantedCapabilities</c>, read by <c>ToolInvocationGovernor</c>) hands
    /// a tool every capability the sandbox model has.
    /// </para>
    /// <para>
    /// A comma inside one entry is split and each token parsed by name, rather than rejected. The
    /// distinction matters because this method also feeds a <em>deny</em> list
    /// (<c>ToolOverrideConfig.DeniedCapabilities</c>), where dropping an entry fails <em>open</em>:
    /// the capability stays granted, and <c>DockerSandboxExecutor</c> reads those same bits to decide
    /// container network access and whether the bind mount is read-only. Refusing
    /// <c>"NetworkAccess,FileWrite"</c> outright would silently convert a working deny into a live
    /// grant on upgrade. Splitting keeps every name the operator wrote meaningful while still
    /// refusing the numeric form, which is the shape that actually loses information.
    /// </para>
    /// </remarks>
    public static ToolCapability ParseCapabilities(IEnumerable<string> names)
    {
        var result = ToolCapability.None;
        foreach (var entry in names)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;

            foreach (var token in entry.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (EnumNameHelper.TryParseName<ToolCapability>(token, out var cap))
                    result |= cap;
            }
        }
        return result;
    }
}
