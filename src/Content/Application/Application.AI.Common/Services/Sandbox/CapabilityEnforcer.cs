using Application.AI.Common.Interfaces.Sandbox;
using Domain.AI.Sandbox;
using Domain.Common;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Sandbox;

/// <summary>
/// Enforces capability-based permission checks by resolving a tool's permission profile and
/// validating the caller's granted capabilities against it, honoring any per-tool
/// <see cref="ToolPermissionProfile.DeniedCapabilities"/> override (#405).
/// </summary>
public sealed class CapabilityEnforcer : ICapabilityEnforcer
{
    private readonly ToolPermissionProfileResolver _resolver;
    private readonly ILogger<CapabilityEnforcer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CapabilityEnforcer"/> class.
    /// </summary>
    /// <param name="resolver">Resolves tool permission profiles from attributes and config.</param>
    /// <param name="logger">Logger for enforcement decision auditing.</param>
    public CapabilityEnforcer(
        ToolPermissionProfileResolver resolver,
        ILogger<CapabilityEnforcer> logger)
    {
        _resolver = resolver;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<ToolPermissionProfile> ResolveProfileAsync(string toolName, CancellationToken ct)
    {
        return Task.FromResult(_resolver.Resolve(toolName));
    }

    /// <inheritdoc />
    public Task<Result> EnforceAsync(
        string toolName,
        ToolCapability grantedCapabilities,
        CancellationToken ct = default)
    {
        var profile = _resolver.Resolve(toolName);

        // A tool whose requirement intersects its own per-tool deny is refused outright, not
        // silently let through on a shrunk requirement — see ToolPermissionProfile's remarks (#405).
        var effectivelyGranted = grantedCapabilities & ~profile.DeniedCapabilities;
        var missing = profile.RequiredCapabilities & ~effectivelyGranted;
        if (missing != ToolCapability.None)
        {
            var missingNames = FormatMissingCapabilities(missing);
            _logger.LogWarning(
                "Tool {ToolName} requires capabilities not granted: {Missing}",
                toolName, missingNames);
            return Task.FromResult(Result.Forbidden(
                $"Tool '{toolName}' requires capabilities not granted: {missingNames}"));
        }

        return Task.FromResult(Result.Success());
    }

    private static string FormatMissingCapabilities(ToolCapability missing)
    {
        var names = Enum.GetValues<ToolCapability>()
            .Where(c => c != ToolCapability.None && missing.HasFlag(c))
            .Select(c => c.ToString());
        return string.Join(", ", names);
    }
}
