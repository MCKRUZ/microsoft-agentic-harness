using System.Collections.Concurrent;
using System.Reflection;
using Domain.Common.Helpers;
using Domain.AI.Sandbox;
using Domain.Common.Config.AI.Sandbox;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.Services.Sandbox;

/// <summary>
/// Resolves the effective <see cref="ToolPermissionProfile"/> for a tool by merging
/// compile-time <see cref="ToolCapabilityAttribute"/> declarations with runtime
/// <see cref="ToolOverrideConfig"/> from appsettings. Uses deny-overrides-allow semantics.
/// </summary>
public sealed class ToolPermissionProfileResolver
{
    private readonly IOptionsMonitor<SandboxConfig> _config;
    private readonly ConcurrentDictionary<string, ToolCapabilityAttribute?> _attributeCache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ToolPermissionProfileResolver"/> class.
    /// </summary>
    /// <param name="config">Sandbox configuration with per-tool overrides.</param>
    public ToolPermissionProfileResolver(IOptionsMonitor<SandboxConfig> config)
    {
        _config = config;
    }

    /// <summary>
    /// Registers a tool type so its <see cref="ToolCapabilityAttribute"/> is available for profile resolution.
    /// Call during DI registration for each keyed tool.
    /// </summary>
    /// <param name="toolName">The keyed DI tool name.</param>
    /// <param name="toolType">The concrete tool implementation type.</param>
    public void RegisterToolType(string toolName, Type toolType)
    {
        _attributeCache[toolName] = toolType.GetCustomAttribute<ToolCapabilityAttribute>();
    }

    /// <summary>
    /// Resolves the effective permission profile by merging the tool's compile-time attribute
    /// (if registered) with runtime configuration overrides.
    /// </summary>
    /// <param name="toolName">The keyed DI tool name.</param>
    /// <returns>The merged permission profile.</returns>
    public ToolPermissionProfile Resolve(string toolName)
    {
        _attributeCache.TryGetValue(toolName, out var attribute);
        _config.CurrentValue.ToolOverrides.TryGetValue(toolName, out var overrideConfig);

        var baseCapabilities = attribute?.Capabilities ?? ToolCapability.None;
        var baseIsolation = attribute?.MinimumIsolation ?? SandboxIsolationLevel.None;

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
