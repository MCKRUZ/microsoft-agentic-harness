using Domain.AI.Sandbox;
using Domain.Common;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Sandbox;

/// <summary>
/// Network-host scoping half of <see cref="CapabilityEnforcer"/> (#418) — see that file's remarks for
/// why this is a separate partial rather than folded into the main file.
/// </summary>
public sealed partial class CapabilityEnforcer
{
    /// <summary>
    /// Checks <paramref name="requestedHosts"/> against the profile's host scoping, when any is
    /// configured. Returns <see langword="null"/> on a pass, or the refusal to return. Mirrors
    /// <see cref="EnforcePathScoping"/>.
    /// </summary>
    private Result? EnforceHostScoping(string toolName, IReadOnlyList<string>? requestedHosts, ToolPermissionProfile profile)
    {
        var hasHostScoping = profile.AllowedHosts.Count > 0 || profile.DeniedHosts.Count > 0;
        if (!hasHostScoping)
            return null;

        if (requestedHosts is null)
        {
            _logger.LogWarning(
                "Tool {ToolName} has host scoping configured but no requested host could be determined for this call",
                toolName);
            return Result.Forbidden(
                $"Tool '{toolName}' has host scoping configured but no requested host could be determined for this call.");
        }

        if (requestedHosts.Count == 0)
            return null;

        // Each configured pattern is normalized once per call here, not once per (requested host ×
        // pattern) pair inside the loop below. A null entry (a literal JSON `null` in DeniedHosts/
        // AllowedHosts, binding into a null List<string> element despite the non-nullable element
        // type) is skipped rather than passed to NormalizeHostForMatch, which would NRE on it.
        var deniedPatterns = profile.DeniedHosts.Where(h => h is not null).Select(NormalizeHostForMatch).ToList();
        var allowedPatterns = profile.AllowedHosts.Where(h => h is not null).Select(NormalizeHostForMatch).ToList();

        if (ValidateHosts(requestedHosts, deniedPatterns, allowedPatterns) is { } hostViolation)
        {
            _logger.LogWarning("Tool {ToolName} host denied: {Host}", toolName, hostViolation);
            return Result.Forbidden($"Tool '{toolName}' host denied: {hostViolation}");
        }

        return null;
    }

    /// <summary>
    /// Returns the first requested host that violates the pre-normalized <paramref name="deniedPatterns"/>/
    /// <paramref name="allowedPatterns"/>, or <see langword="null"/> if every host is permitted.
    /// Deny-overrides-allow, mirroring <see cref="ValidatePaths"/> (#418, restored from pre-#405 history).
    /// </summary>
    private static string? ValidateHosts(
        IReadOnlyList<string> requestedHosts, IReadOnlyList<string> deniedPatterns, IReadOnlyList<string> allowedPatterns)
    {
        foreach (var host in requestedHosts)
        {
            var normalizedHost = NormalizeHostForMatch(host);

            // Closes only the empty-string case of the gap ValidatePaths' unconditional rejection
            // closes more broadly for paths (#595 code-review) — an empty host is refused regardless
            // of whether AllowedHosts is configured, not only when it fails to match an allow entry.
            // Without this, a deny-list-only configuration (no AllowedHosts, so the allow-check below
            // never runs) would silently admit an empty host that matches no configured deny pattern.
            // NOT a full mirror of ValidatePaths: a non-empty but malformed host (embedded control
            // characters, oversized input) still falls through to ordinary deny/allow matching here,
            // unlike a path, which SecureInputValidatorHelper.ValidateFilePath rejects outright.
            // Broadening this to match is tracked separately, not part of what #595 set out to fix.
            if (string.IsNullOrEmpty(normalizedHost))
                return host;

            if (deniedPatterns.Any(pattern => HostPatternMatches(normalizedHost, pattern)))
                return host;

            if (allowedPatterns.Count > 0 && !allowedPatterns.Any(pattern => HostPatternMatches(normalizedHost, pattern)))
                return host;
        }

        return null;
    }

    /// <summary>
    /// Matches an already-<see cref="NormalizeHostForMatch"/>d host against an already-normalized
    /// pattern, which may be an exact host name or a <c>*.suffix</c> wildcard.
    /// </summary>
    private static bool HostPatternMatches(string normalizedHost, string normalizedPattern)
    {
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
    /// comparable host name. A value that parses as an absolute URI is reduced via <see cref="Uri.Host"/>
    /// itself, which correctly drops a scheme, userinfo (<c>user@</c>), port, and path/query in one
    /// step — <em>not</em> an ad-hoc scan for <c>"://"</c>, which matches the first occurrence anywhere
    /// in the string rather than only a leading scheme and so can be pointed at an unrelated embedded
    /// URL later in the value. A value that isn't itself an absolute URI falls back to a trailing-port
    /// strip and a root-terminating FQDN dot trim. Applying the identical reduction to both sides of a
    /// <see cref="HostPatternMatches"/> comparison is what keeps an operator's plain <c>"evil.com"</c>
    /// entry matching every equivalent spelling of that same host a caller might supply.
    /// </summary>
    private static string NormalizeHostForMatch(string value)
    {
        var trimmed = value.Trim();

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
            return uri.Host.TrimEnd('.');

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
}
