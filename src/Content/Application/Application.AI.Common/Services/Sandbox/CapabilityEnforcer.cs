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
        IReadOnlyList<string>? requestedPaths = null,
        IReadOnlyList<string>? requestedHosts = null,
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

        // #418: a profile with path/host scoping configured but no requested value to check against
        // must refuse, not silently allow — this is deliberately NOT `is { Count: > 0 }`, which is
        // the fail-open shape #405 shipped (null and [] were treated identically, so scoping was
        // configured but never actually enforced against a call whose resource usage was simply
        // never determined). See ToolCallResourceRequest's remarks for why null vs. empty matters.
        var hasPathScoping = profile.AllowedPaths.Count > 0 || profile.DeniedPaths.Count > 0;
        if (hasPathScoping)
        {
            if (requestedPaths is null)
            {
                _logger.LogWarning(
                    "Tool {ToolName} has path scoping configured but no requested path could be determined for this call",
                    toolName);
                return Task.FromResult(Result.Forbidden(
                    $"Tool '{toolName}' has path scoping configured but no requested path could be determined for this call."));
            }

            if (requestedPaths.Count > 0 && ValidatePaths(requestedPaths, profile) is { } pathViolation)
            {
                _logger.LogWarning("Tool {ToolName} path denied: {Path}", toolName, pathViolation);
                return Task.FromResult(Result.Forbidden($"Tool '{toolName}' path denied: {pathViolation}"));
            }
        }

        var hasHostScoping = profile.AllowedHosts.Count > 0 || profile.DeniedHosts.Count > 0;
        if (hasHostScoping)
        {
            if (requestedHosts is null)
            {
                _logger.LogWarning(
                    "Tool {ToolName} has host scoping configured but no requested host could be determined for this call",
                    toolName);
                return Task.FromResult(Result.Forbidden(
                    $"Tool '{toolName}' has host scoping configured but no requested host could be determined for this call."));
            }

            if (requestedHosts.Count > 0 && ValidateHosts(requestedHosts, profile) is { } hostViolation)
            {
                _logger.LogWarning("Tool {ToolName} host denied: {Host}", toolName, hostViolation);
                return Task.FromResult(Result.Forbidden($"Tool '{toolName}' host denied: {hostViolation}"));
            }
        }

        return Task.FromResult(Result.Success());
    }

    /// <summary>
    /// Returns the first requested path that violates <paramref name="profile"/>'s deny/allow lists,
    /// or <see langword="null"/> if every path is permitted. Deny-overrides-allow: a deny match wins
    /// even when the same path also matches an allow entry (#418, restored from pre-#405 history).
    /// </summary>
    private static string? ValidatePaths(IReadOnlyList<string> requestedPaths, ToolPermissionProfile profile)
    {
        foreach (var path in requestedPaths)
        {
            var normalized = NormalizePath(path);

            if (profile.DeniedPaths.Any(denied => IsPathWithin(normalized, NormalizePath(denied))))
                return path;

            if (profile.AllowedPaths.Count > 0 &&
                !profile.AllowedPaths.Any(allowed => IsPathWithin(normalized, NormalizePath(allowed))))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>
    /// Determines whether <paramref name="candidate"/> is the same as, or a descendant of,
    /// <paramref name="boundary"/>, comparing on path-segment boundaries rather than raw string
    /// prefixes. This prevents sibling-directory bypass (e.g. boundary "C:/sandbox/work" must NOT
    /// match "C:/sandbox/work-evil"). Both inputs are expected to be already normalized via
    /// <see cref="NormalizePath"/> (slash-separated, no empty/relative segments, no trailing slash).
    /// </summary>
    private static bool IsPathWithin(string candidate, string boundary)
    {
        // An empty boundary (e.g. root after normalization) confines everything.
        if (boundary.Length == 0)
            return true;

        if (candidate.Equals(boundary, StringComparison.OrdinalIgnoreCase))
            return true;

        // Descendant must start with "boundary/" so the next character is a true segment boundary.
        return candidate.Length > boundary.Length
            && candidate[boundary.Length] == '/'
            && candidate.StartsWith(boundary, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the first requested host that violates <paramref name="profile"/>'s deny/allow lists,
    /// or <see langword="null"/> if every host is permitted. Deny-overrides-allow, mirroring
    /// <see cref="ValidatePaths"/> (#418, restored from pre-#405 history).
    /// </summary>
    private static string? ValidateHosts(IReadOnlyList<string> requestedHosts, ToolPermissionProfile profile)
    {
        foreach (var host in requestedHosts)
        {
            if (profile.DeniedHosts.Any(denied => HostMatches(host, denied)))
                return host;

            if (profile.AllowedHosts.Count > 0 &&
                !profile.AllowedHosts.Any(allowed => HostMatches(host, allowed)))
            {
                return host;
            }
        }

        return null;
    }

    /// <summary>
    /// Matches <paramref name="host"/> (port stripped) against <paramref name="pattern"/>, which may
    /// be an exact host name or a <c>*.suffix</c> wildcard.
    /// </summary>
    private static bool HostMatches(string host, string pattern)
    {
        var normalizedHost = StripPort(host);

        if (pattern.StartsWith("*."))
        {
            var suffix = pattern[1..];
            return normalizedHost.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                   || normalizedHost.Equals(pattern[2..], StringComparison.OrdinalIgnoreCase);
        }

        return normalizedHost.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static string StripPort(string host)
    {
        var colonIndex = host.LastIndexOf(':');
        return colonIndex > 0 && host[(colonIndex + 1)..].All(char.IsDigit)
            ? host[..colonIndex]
            : host;
    }

    /// <summary>
    /// Normalizes path separators to <c>/</c> and resolves <c>.</c>/<c>..</c> segments so
    /// <see cref="IsPathWithin"/> compares like-for-like regardless of how the caller or the config
    /// wrote a path.
    /// </summary>
    private static string NormalizePath(string path)
    {
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>();
        foreach (var segment in segments)
        {
            if (segment == ".") continue;
            if (segment == ".." && result.Count > 0 && result[^1] != "..")
                result.RemoveAt(result.Count - 1);
            else if (segment != "..")
                result.Add(segment);
        }
        return string.Join('/', result);
    }

    private static string FormatMissingCapabilities(ToolCapability missing)
    {
        var names = Enum.GetValues<ToolCapability>()
            .Where(c => c != ToolCapability.None && missing.HasFlag(c))
            .Select(c => c.ToString());
        return string.Join(", ", names);
    }
}
