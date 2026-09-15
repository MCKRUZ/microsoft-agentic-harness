using Domain.AI.Sandbox;
using Domain.Common;
using Domain.Common.Helpers;
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
        var deniedPatterns = profile.DeniedHosts.Where(h => h is not null).Select(HostPatternNormalizer.NormalizeHostForMatch).ToList();
        var allowedPatterns = profile.AllowedHosts.Where(h => h is not null).Select(HostPatternNormalizer.NormalizeHostForMatch).ToList();

        // #635 LOW: a typo'd or malformed deny/allow entry normalizes to a string ValidateHost
        // would reject on the requested-host side (SecureInputValidatorHelper.ValidateHost is never
        // run on the CONFIGURED side, only the requested side, in ValidateHosts below) — silently
        // becoming a permanent no-op with no signal to the operator that their configuration is
        // inert. Advisory only: log and keep evaluating, since a malformed pattern is harmless (it
        // just never matches anything) rather than a reason to fail every call closed.
        WarnIfPatternIsInert(toolName, "DeniedHosts", deniedPatterns);
        WarnIfPatternIsInert(toolName, "AllowedHosts", allowedPatterns);

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
            var normalizedHost = HostPatternNormalizer.NormalizeHostForMatch(host);

            // Rejects the whole class of malformed input outright — empty, oversized, embedded
            // NUL/control characters, or anything that isn't syntactically a valid host — mirroring
            // ValidatePaths' unconditional NormalizeRequestedPath rejection for paths (#605; #595
            // closed only the narrower empty-string case of this gap). Validated against the
            // NORMALIZED value, not the raw requested value: a legitimate requested host can arrive
            // as a full absolute URI, which NormalizeHostForMatch's own job is to reduce to a bare
            // host — SecureInputValidatorHelper.ValidateHost expects that bare-host shape, so running
            // it against the raw value would reject every URI-shaped legitimate host.
            if (!SecureInputValidatorHelper.ValidateHost(normalizedHost))
                return host;

            if (deniedPatterns.Any(pattern => HostPatternMatches(normalizedHost, pattern)))
                return host;

            if (allowedPatterns.Count > 0 && !allowedPatterns.Any(pattern => HostPatternMatches(normalizedHost, pattern)))
                return host;
        }

        return null;
    }

    /// <summary>
    /// Matches an already-<see cref="HostPatternNormalizer.NormalizeHostForMatch"/>d host against an
    /// already-normalized pattern, which may be an exact host name or a <c>*.suffix</c> wildcard.
    /// </summary>
    private static bool HostPatternMatches(string normalizedHost, string normalizedPattern)
    {
        if (HostPatternNormalizer.HasWildcardPrefix(normalizedPattern))
        {
            var suffix = normalizedPattern[1..];
            return normalizedHost.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                   || normalizedHost.Equals(normalizedPattern[2..], StringComparison.OrdinalIgnoreCase);
        }

        return normalizedHost.Equals(normalizedPattern, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Logs one warning per already-normalized <paramref name="patterns"/> entry that
    /// <see cref="SecureInputValidatorHelper.ValidateHost"/> would reject — #635 LOW: such an entry
    /// can never match any requested host (every requested host is validated the same way in
    /// <see cref="ValidateHosts"/>), so it is a silent, permanent no-op in the operator's
    /// configuration unless something says so. Also enforced at startup (#647): a bad pattern only
    /// ever surfaces here on the next call to a tool with host scoping configured, which for a
    /// rarely-invoked tool could be a long time after the host boots — see
    /// <c>SandboxConfigValidator</c>'s identical check over <c>SandboxConfig.ToolOverrides</c>, run
    /// once at process start regardless of traffic. Kept here too rather than removed: the runtime
    /// warning still fires for any config bound outside the validated <c>AppConfig:AI:SandboxCapabilities</c>
    /// path (e.g. a test fixture constructing <c>ToolPermissionProfile</c> directly), which the
    /// startup validator never sees.
    /// </summary>
    private void WarnIfPatternIsInert(string toolName, string configKey, IReadOnlyList<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (!HostPatternNormalizer.IsMatchableWhenNormalized(pattern))
            {
                _logger.LogWarning(
                    "Tool {ToolName} has a {ConfigKey} entry that normalizes to an invalid host and can never match any requested host: {Pattern}",
                    toolName, configKey, pattern);
            }
        }
    }
}
