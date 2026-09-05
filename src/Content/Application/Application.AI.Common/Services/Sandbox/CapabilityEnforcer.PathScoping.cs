using Domain.AI.Sandbox;
using Domain.Common;
using Domain.Common.Helpers;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Sandbox;

/// <summary>
/// Filesystem-path scoping half of <see cref="CapabilityEnforcer"/> (#418) — see that file's remarks
/// for why this is a separate partial rather than folded into the main file.
/// </summary>
public sealed partial class CapabilityEnforcer
{
    /// <summary>
    /// Checks <paramref name="requestedPaths"/> against the profile's path scoping, when any is
    /// configured. Returns <see langword="null"/> on a pass, or the refusal to return.
    /// </summary>
    /// <remarks>
    /// #418: a profile with path scoping configured but no requested value to check against must
    /// refuse, not silently allow — this is deliberately NOT <c>is { Count: > 0 }</c>, which is the
    /// fail-open shape #405 shipped (null and <c>[]</c> were treated identically, so scoping was
    /// configured but never actually enforced against a call whose resource usage was simply never
    /// determined). See <see cref="Domain.AI.Sandbox.ToolCallResourceRequest"/>'s remarks for why
    /// null vs. empty matters.
    /// </remarks>
    private Result? EnforcePathScoping(string toolName, IReadOnlyList<string>? requestedPaths, ToolPermissionProfile profile)
    {
        var hasPathScoping = profile.AllowedPaths.Count > 0 || profile.DeniedPaths.Count > 0;
        if (!hasPathScoping)
            return null;

        if (requestedPaths is null)
        {
            _logger.LogWarning(
                "Tool {ToolName} has path scoping configured but no requested path could be determined for this call",
                toolName);
            return Result.Forbidden(
                $"Tool '{toolName}' has path scoping configured but no requested path could be determined for this call.");
        }

        if (requestedPaths.Count == 0)
            return null;

        // Each configured boundary is normalized (and, when a canonicalizer is registered,
        // link-resolved via a real filesystem stat) at most once per call here, not once per
        // (requested path × boundary) pair inside the loop below — a profile with several entries
        // and a multi-path request would otherwise repeat that filesystem I/O for every pair.
        var deniedBoundaries = profile.DeniedPaths.Select(NormalizeAndCanonicalize).ToList();
        var allowedBoundaries = profile.AllowedPaths.Select(NormalizeBoundary).ToList();

        if (ValidatePaths(requestedPaths, deniedBoundaries, allowedBoundaries) is { } pathViolation)
        {
            _logger.LogWarning("Tool {ToolName} path denied: {Path}", toolName, pathViolation);
            return Result.Forbidden($"Tool '{toolName}' path denied: {pathViolation}");
        }

        return null;
    }

    /// <summary>
    /// Returns the first requested path that violates the pre-normalized <paramref name="deniedBoundaries"/>/
    /// <paramref name="allowedBoundaries"/>, or <see langword="null"/> if every path is permitted.
    /// Deny-overrides-allow: a deny match wins even when the same path also matches an allow entry
    /// (#418, restored from pre-#405 history).
    /// </summary>
    private string? ValidatePaths(
        IReadOnlyList<string> requestedPaths, IReadOnlyList<string?> deniedBoundaries, IReadOnlyList<string> allowedBoundaries)
    {
        foreach (var path in requestedPaths)
        {
            // A model-supplied path this class cannot safely resolve — one carrying a traversal
            // pattern, a relative path, or one the runtime rejects outright — is treated as a
            // violation rather than compared. CI caught the alternative: a naive normalizer silently
            // dropped a leading ".." instead of resolving it, so "../secrets/creds.txt" matched no
            // configured boundary at all. CapabilityEnforcer has no base directory of its own to
            // resolve a relative path against (that is IFileSystemService's own, separately-configured
            // concern), so refusing outright — mirroring SandboxedPathGuard.ResolveAndValidate's own
            // first check — is the only answer that cannot be tricked into comparing the wrong path.
            if (NormalizeRequestedPath(path) is not { } normalized)
                return path;

            // A configured deny entry the runtime could not normalize (null) is treated as matching
            // every candidate, not excluded from the check — unlike an allow entry below, treating an
            // unparsable deny entry as "never matches" would silently turn a misconfigured deny rule
            // into a no-op instead of the refusal it was written to enforce, which is the fail-open
            // shape this whole mechanism exists to prevent.
            if (deniedBoundaries.Any(denied => denied is null || IsPathWithin(normalized, denied)))
                return path;

            if (allowedBoundaries.Count > 0 && !allowedBoundaries.Any(allowed => IsPathWithin(normalized, allowed)))
                return path;
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
    /// pattern, is not fully qualified, or the runtime cannot parse it at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <em>relative</em> path is refused outright, not resolved — CI caught the alternative:
    /// resolving it against <see cref="PathScope.Normalize"/>'s implicit base (the process's current
    /// directory) answers a different question than the one that matters, because the tool that
    /// actually opens the file resolves the identical string against its own, separately-configured
    /// sandbox base path (<c>SandboxedPathGuard.ResolveRelative</c>) — a base this class has no
    /// visibility into and no business assuming. The two resolutions can name different files
    /// entirely, so a relative path is unsafe to compare here at all, exactly like a traversal
    /// pattern is.
    /// </para>
    /// <para>
    /// The fully-qualified check lives inside <see cref="NormalizeAndCanonicalize"/> itself, applied
    /// identically to a requested path and a configured boundary — CI caught the alternative: checking
    /// it only here left a <em>relative</em> <c>DeniedPaths</c>/<c>AllowedPaths</c> config entry to
    /// silently resolve against the process's CWD via <see cref="PathScope.Normalize"/>, the exact
    /// fail-open shape this guard exists to prevent, just on the configured side instead of the
    /// requested side.
    /// </para>
    /// </remarks>
    private string? NormalizeRequestedPath(string path) =>
        SecureInputValidatorHelper.ValidateFilePath(path) ? NormalizeAndCanonicalize(path) : null;

    /// <summary>
    /// Normalizes and canonicalizes an operator-configured <em>allow</em> boundary the same way as a
    /// requested path (#418) — both sides must go through identical treatment, or a boundary reached
    /// only through a symlink would compare unequal to an already-resolved candidate. Falls back to
    /// the raw configured string on a normalization failure, which for an allow entry safely excludes
    /// it (a raw, non-normalized string will not match a normalized candidate) rather than throwing —
    /// a typo in one operator entry should degrade that one comparison, not take down every call that
    /// consults the list. Deny entries are handled directly in <see cref="ValidatePaths"/> instead,
    /// where the same fallback would be fail-open rather than fail-closed.
    /// </summary>
    private string NormalizeBoundary(string configuredPath) => NormalizeAndCanonicalize(configuredPath) ?? configuredPath;

    /// <summary>
    /// Resolves <paramref name="path"/> to its absolute form via <see cref="PathScope.Normalize"/> —
    /// the same routine <c>SandboxedPathGuard</c> normalizes real file access through, so a genuine
    /// <c>..</c> segment is actually resolved rather than silently discarded, and platform quirks
    /// (e.g. a Windows trailing-dot path component) are handled identically on both sides of the
    /// comparison — then link-resolves through <see cref="_pathCanonicalizer"/> when one is
    /// registered. Returns <see langword="null"/> on any input that is not already fully qualified
    /// (checked via <c>Path.IsPathFullyQualified</c>, not <c>Path.IsPathRooted</c> — the latter answers
    /// <see langword="true"/> for a Windows drive-relative path like <c>"C:secrets\creds.txt"</c>,
    /// which still resolves against the current directory on that drive, not an absolute location) or
    /// that the runtime cannot parse at all. Applied to both a requested path and a configured boundary
    /// identically: neither side has any base directory of its own to resolve a relative path against
    /// that the other side would agree with.
    /// </summary>
    private string? NormalizeAndCanonicalize(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            return null;

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
}
