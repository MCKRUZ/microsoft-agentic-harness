using Application.AI.Common.Interfaces.Sandbox;
using Domain.AI.Sandbox;
using Domain.Common;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Sandbox;

/// <summary>
/// Enforces capability-based permission checks by resolving tool profiles
/// and validating granted capabilities, paths, and hosts against deny-overrides-allow rules.
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

        var missing = profile.RequiredCapabilities & ~grantedCapabilities;
        if (missing != ToolCapability.None)
        {
            var missingNames = FormatMissingCapabilities(missing);
            _logger.LogWarning(
                "Tool {ToolName} requires capabilities not granted: {Missing}",
                toolName, missingNames);
            return Task.FromResult(Result.Forbidden(
                $"Tool '{toolName}' requires capabilities not granted: {missingNames}"));
        }

        if (requestedPaths is { Count: > 0 })
        {
            var pathViolation = ValidatePaths(requestedPaths, profile);
            if (pathViolation is not null)
            {
                _logger.LogWarning(
                    "Tool {ToolName} path denied: {Path}",
                    toolName, pathViolation);
                return Task.FromResult(Result.Forbidden(
                    $"Tool '{toolName}' path denied: {pathViolation}"));
            }
        }

        if (requestedHosts is { Count: > 0 })
        {
            var hostViolation = ValidateHosts(requestedHosts, profile);
            if (hostViolation is not null)
            {
                _logger.LogWarning(
                    "Tool {ToolName} host denied: {Host}",
                    toolName, hostViolation);
                return Task.FromResult(Result.Forbidden(
                    $"Tool '{toolName}' host denied: {hostViolation}"));
            }
        }

        return Task.FromResult(Result.Success());
    }

    private static string? ValidatePaths(
        IReadOnlyList<string> requestedPaths,
        ToolPermissionProfile profile)
    {
        foreach (var path in requestedPaths)
        {
            var normalized = NormalizePath(path);

            if (profile.DeniedPaths.Any(denied =>
                normalized.StartsWith(NormalizePath(denied), StringComparison.OrdinalIgnoreCase)))
            {
                return path;
            }

            if (profile.AllowedPaths.Count > 0 &&
                !profile.AllowedPaths.Any(allowed =>
                    normalized.StartsWith(NormalizePath(allowed), StringComparison.OrdinalIgnoreCase)))
            {
                return path;
            }
        }

        return null;
    }

    private static string? ValidateHosts(
        IReadOnlyList<string> requestedHosts,
        ToolPermissionProfile profile)
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
