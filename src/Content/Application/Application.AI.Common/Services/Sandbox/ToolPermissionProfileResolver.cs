using System.Collections.Concurrent;
using System.Reflection;
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

        var overrideIsolation = Enum.TryParse<SandboxIsolationLevel>(
            overrideConfig.MinimumIsolation, ignoreCase: true, out var parsed)
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
    /// <see cref="ToolCapability"/> flags value. Invalid names are silently ignored.
    /// </summary>
    public static ToolCapability ParseCapabilities(IEnumerable<string> names)
    {
        var result = ToolCapability.None;
        foreach (var name in names)
        {
            if (Enum.TryParse<ToolCapability>(name, ignoreCase: true, out var cap))
                result |= cap;
        }
        return result;
    }
}
