using Application.AI.Common.Interfaces.Sandbox;
using Domain.AI.Sandbox;
using Domain.Common;
using Domain.Common.Helpers;
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
    private readonly IPathCanonicalizer? _pathCanonicalizer;

    /// <summary>
    /// Initializes a new instance of the <see cref="CapabilityEnforcer"/> class.
    /// </summary>
    /// <param name="resolver">Resolves tool permission profiles from attributes and config.</param>
    /// <param name="logger">Logger for enforcement decision auditing.</param>
    /// <param name="pathCanonicalizer">
    /// Resolves symlinks/junctions before a path-scoping comparison (#418's CI hardening), so
    /// <see cref="ValidatePaths"/> cannot be defeated by a link the sandbox itself would follow.
    /// Optional: a host that doesn't register one still gets the normalized-string comparison, just
    /// without link resolution — see <see cref="IPathCanonicalizer"/>'s own remarks.
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
    private string? ValidatePaths(IReadOnlyList<string> requestedPaths, ToolPermissionProfile profile)
    {
        foreach (var path in requestedPaths)
        {
            // A model-supplied path this class cannot safely resolve — one carrying a traversal
            // pattern, or one the runtime rejects outright — is treated as a violation rather than
            // compared. CI caught the alternative: a naive normalizer silently dropped a leading
            // ".." instead of resolving it, so "../secrets/creds.txt" matched no configured
            // boundary at all. CapabilityEnforcer has no base directory of its own to resolve a
            // relative traversal against (that is IFileSystemService's own, separately-configured
            // concern), so refusing outright — mirroring SandboxedPathGuard.ResolveAndValidate's own
            // first check — is the only answer that cannot be tricked into comparing the wrong path.
            if (NormalizeRequestedPath(path) is not { } normalized)
                return path;

            if (profile.DeniedPaths.Any(denied => IsPathWithin(normalized, NormalizeBoundary(denied))))
                return path;

            if (profile.AllowedPaths.Count > 0 &&
                !profile.AllowedPaths.Any(allowed => IsPathWithin(normalized, NormalizeBoundary(allowed))))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>
    /// Determines whether <paramref name="candidate"/> is the same as, or a descendant of,
    /// <paramref name="boundary"/>, comparing on path-segment boundaries rather than raw string
    /// prefixes via <see cref="PathScope.IsSameOrUnderNormalized"/> — the same primitive
    /// <c>SandboxedPathGuard</c> compares real file access against, so the two cannot disagree about
    /// which file a path names. Both inputs are expected to be already normalized (and, when a
    /// canonicalizer is available, link-resolved) via <see cref="NormalizeAndCanonicalize"/>.
    /// </summary>
    private static bool IsPathWithin(string candidate, string boundary) =>
        // An empty boundary (root, fully trimmed) confines everything — preserved as an explicit
        // case because PathScope.IsSameOrUnderNormalized's own separator-prefixed check does not
        // reduce to "matches everything" identically on every platform.
        boundary.Length == 0 || PathScope.IsSameOrUnderNormalized(candidate, boundary);

    /// <summary>
    /// Normalizes and canonicalizes a model-supplied path for a scoping comparison, refusing
    /// (returning <see langword="null"/>) rather than guessing when the input carries a traversal
    /// pattern, is not already rooted, or the runtime cannot parse it at all.
    /// </summary>
    /// <remarks>
    /// A <em>relative</em> path is refused outright, not resolved — CI caught the alternative:
    /// resolving it against <see cref="PathScope.Normalize"/>'s implicit base (the process's current
    /// directory) answers a different question than the one that matters, because the tool that
    /// actually opens the file resolves the identical string against its own, separately-configured
    /// sandbox base path (<c>SandboxedPathGuard.ResolveRelative</c>) — a base this class has no
    /// visibility into and no business assuming. The two resolutions can name different files
    /// entirely, so a relative path is unsafe to compare here at all, exactly like a traversal
    /// pattern is.
    /// </remarks>
    private string? NormalizeRequestedPath(string path) =>
        Path.IsPathRooted(path) && SecureInputValidatorHelper.ValidateFilePath(path)
            ? NormalizeAndCanonicalize(path)
            : null;

    /// <summary>
    /// Normalizes and canonicalizes an operator-configured boundary the same way as a requested path
    /// (#418) — both sides must go through identical treatment, or a boundary reached only through a
    /// symlink would compare unequal to an already-resolved candidate. Falls back to the raw
    /// configured string on a normalization failure rather than refusing: a typo in one operator
    /// entry should degrade that one comparison, not take down every call that consults the list.
    /// </summary>
    private string NormalizeBoundary(string configuredPath) => NormalizeAndCanonicalize(configuredPath) ?? configuredPath;

    /// <summary>
    /// Resolves <paramref name="path"/> to its absolute form via <see cref="PathScope.Normalize"/> —
    /// the same routine <c>SandboxedPathGuard</c> normalizes real file access through, so a genuine
    /// <c>..</c> segment is actually resolved rather than silently discarded, and platform quirks
    /// (e.g. a Windows trailing-dot path component) are handled identically on both sides of the
    /// comparison — then link-resolves through <see cref="_pathCanonicalizer"/> when one is
    /// registered. Returns <see langword="null"/> on any input the runtime cannot parse.
    /// </summary>
    private string? NormalizeAndCanonicalize(string path)
    {
        try
        {
            var normalized = PathScope.Normalize(path);
            return _pathCanonicalizer?.Canonicalize(normalized) ?? normalized;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
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
    /// Matches <paramref name="host"/> against <paramref name="pattern"/>, which may be an exact host
    /// name or a <c>*.suffix</c> wildcard. Both sides go through <see cref="NormalizeHostForMatch"/>
    /// identically — a configured pattern is operator-authored, not attacker-controlled, but a
    /// port/scheme/whitespace mismatch on that side is just as fail-open as one on the requested host,
    /// so both are held to the same normalization rather than only the request.
    /// </summary>
    private static bool HostMatches(string host, string pattern)
    {
        var normalizedHost = NormalizeHostForMatch(host);
        var normalizedPattern = NormalizeHostForMatch(pattern);

        if (normalizedPattern.StartsWith("*."))
        {
            var suffix = normalizedPattern[1..];
            return normalizedHost.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                   || normalizedHost.Equals(normalizedPattern[2..], StringComparison.OrdinalIgnoreCase);
        }

        return normalizedHost.Equals(normalizedPattern, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reduces a host value — whether a requested host or a configured deny/allow entry — to a bare,
    /// comparable host name: trims whitespace, strips a URL scheme and any path/query that followed it
    /// (<c>"https://Evil.com/x"</c> → <c>"Evil.com"</c>), strips a trailing port, and trims a
    /// root-terminating FQDN dot. Applying the identical reduction to both sides of a
    /// <see cref="HostMatches"/> comparison is what keeps an operator's plain <c>"evil.com"</c> entry
    /// matching every equivalent spelling of that same host a caller might supply.
    /// </summary>
    private static string NormalizeHostForMatch(string value)
    {
        var trimmed = value.Trim();

        var schemeIndex = trimmed.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex >= 0)
            trimmed = trimmed[(schemeIndex + 3)..];

        var slashIndex = trimmed.IndexOf('/');
        if (slashIndex >= 0)
            trimmed = trimmed[..slashIndex];

        return StripPort(trimmed).TrimEnd('.');
    }

    /// <summary>
    /// Strips a trailing <c>:port</c> from <paramref name="host"/>, recognizing the bracketed IPv6
    /// form (<c>[::1]:443</c>) and leaving a <em>bare</em> IPv6 literal (<c>::1</c>) untouched — more
    /// than one colon with no brackets is never a host:port pair, so treating the last colon as a
    /// port separator there would truncate the address itself (<c>::1</c> otherwise becomes <c>:</c>
    /// and can never match a configured deny/allow entry for it).
    /// </summary>
    private static string StripPort(string host)
    {
        if (host.StartsWith('['))
        {
            var closeBracket = host.IndexOf(']');
            return closeBracket > 0 ? host[1..closeBracket] : host;
        }

        if (host.Count(c => c == ':') != 1)
            return host;

        var colonIndex = host.IndexOf(':');
        return colonIndex > 0 && host[(colonIndex + 1)..].All(char.IsDigit)
            ? host[..colonIndex]
            : host;
    }

    private static string FormatMissingCapabilities(ToolCapability missing)
    {
        var names = Enum.GetValues<ToolCapability>()
            .Where(c => c != ToolCapability.None && missing.HasFlag(c))
            .Select(c => c.ToString());
        return string.Join(", ", names);
    }
}
