using Application.AI.Common.Interfaces.Sandbox;
using Domain.AI.Sandbox;
using Domain.Common;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Sandbox;

/// <summary>
/// Enforces capability-based permission checks by resolving a tool's permission profile and
/// validating the caller's granted capabilities against it, honoring any per-tool
/// <see cref="ToolPermissionProfile.DeniedCapabilities"/> override (#405), and validating any
/// requested filesystem paths/network hosts against the profile's deny-overrides-allow scoping (#418).
/// </summary>
/// <remarks>
/// Split by concern into partial classes — <c>CapabilityEnforcer.PathScoping.cs</c> and
/// <c>CapabilityEnforcer.HostScoping.cs</c> — the same pattern this codebase already uses for a large
/// class with genuinely independent responsibilities (see <c>PlanExecutor.Scheduling.cs</c>/
/// <c>PlanExecutor.Recovery.cs</c>, <c>ToolInvocationGovernor</c>). Path scoping and host scoping each
/// own their own normalization rules and fail-open/fail-closed reasoning and share nothing with
/// capability-flag checking beyond "same profile object, same deny-overrides-allow pattern" — this
/// file keeps only the shared entry point and the capability check itself.
/// </remarks>
public sealed partial class CapabilityEnforcer : ICapabilityEnforcer
{
    private readonly ToolPermissionProfileResolver _resolver;
    private readonly ILogger<CapabilityEnforcer> _logger;
    private readonly IPathCanonicalizer? _pathCanonicalizer;

    /// <summary>
    /// Initializes a new instance of the <see cref="CapabilityEnforcer"/> class.
    /// </summary>
    /// <param name="resolver">Resolves tool permission profiles from attributes and config.</param>
    /// <param name="logger">Logger for enforcement decision auditing.</param>
    /// <param name="pathCanonicalizer">
    /// Resolves symlinks/junctions before a path-scoping comparison (#418's CI hardening), so the
    /// path-scoping check cannot be defeated by a link the sandbox itself would follow. Optional: a
    /// host that doesn't register one still gets the normalized-string comparison, just without link
    /// resolution — see <see cref="IPathCanonicalizer"/>'s own remarks.
    /// </param>
    public CapabilityEnforcer(
        ToolPermissionProfileResolver resolver,
        ILogger<CapabilityEnforcer> logger,
        IPathCanonicalizer? pathCanonicalizer = null)
    {
        _resolver = resolver;
        _logger = logger;
        _pathCanonicalizer = pathCanonicalizer;
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
        IReadOnlyList<string>? requestedPaths = null,
        IReadOnlyList<string>? requestedHosts = null,
        CancellationToken ct = default)
    {
        var profile = _resolver.Resolve(toolName);

        if (EnforceCapabilities(toolName, grantedCapabilities, profile) is { } capabilityViolation)
            return Task.FromResult(capabilityViolation);

        if (EnforcePathScoping(toolName, requestedPaths, profile) is { } pathViolation)
            return Task.FromResult(pathViolation);

        if (EnforceHostScoping(toolName, requestedHosts, profile) is { } hostViolation)
            return Task.FromResult(hostViolation);

        return Task.FromResult(Result.Success());
    }

    /// <summary>
    /// Checks the caller's granted capabilities (narrowed by any per-tool deny override) against the
    /// tool's requirement. Returns <see langword="null"/> on a pass, or the refusal to return.
    /// </summary>
    private Result? EnforceCapabilities(string toolName, ToolCapability grantedCapabilities, ToolPermissionProfile profile)
    {
        // A tool whose requirement intersects its own per-tool deny is refused outright, not
        // silently let through on a shrunk requirement — see ToolPermissionProfile's remarks (#405).
        var effectivelyGranted = grantedCapabilities & ~profile.DeniedCapabilities;
        var missing = profile.RequiredCapabilities & ~effectivelyGranted;
        if (missing == ToolCapability.None)
            return null;

        var missingNames = FormatMissingCapabilities(missing);
        _logger.LogWarning("Tool {ToolName} requires capabilities not granted: {Missing}", toolName, missingNames);
        return Result.Forbidden($"Tool '{toolName}' requires capabilities not granted: {missingNames}");
    }

    private static string FormatMissingCapabilities(ToolCapability missing)
    {
        var names = Enum.GetValues<ToolCapability>()
            .Where(c => c != ToolCapability.None && missing.HasFlag(c))
            .Select(c => c.ToString());
        return string.Join(", ", names);
    }
}
