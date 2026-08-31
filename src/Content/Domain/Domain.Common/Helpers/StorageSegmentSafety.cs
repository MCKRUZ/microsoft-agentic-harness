using System.Text.RegularExpressions;

namespace Domain.Common.Helpers;

/// <summary>
/// Shape checks common to every value that becomes a filesystem directory-name segment (a tool-result
/// scope id, a conversation id, a plan run id), independent of whatever charset a caller applies first.
/// </summary>
/// <remarks>
/// A charset allowlist alone cannot express these three shapes: a single ASCII letter followed by
/// <c>:</c> is charset-legal but drive-rooted on Windows (<see cref="Path.IsPathRooted(string)"/>
/// measures <c>"C:"</c> as <see langword="true"/>, but a colon used as an internal separator between
/// multi-character segments — e.g. <c>"conv-1:step-5"</c> — is not); <c>"."</c> and <c>".."</c> are
/// charset-legal but resolve to the current/parent directory once combined into a path; a trailing dot
/// is charset-legal but Windows silently strips it, letting two different-looking values collide onto
/// one directory.
/// <para>
/// Extracted (/code-review, /simplify findings) after this exact four-part check — the charset plus
/// these three shape rules — was independently hand-copied into
/// <c>FileSystemToolResultStore.SanitizeSessionSegment</c>, <c>RunConversationCommandValidator</c>,
/// <c>RunOrchestratedTaskCommandValidator</c>, and <c>PlanRunExecutor</c>'s <c>RunId</c> check. Two of
/// this allowlist's own bounds already regressed twice from being hand-synced by comment alone (the
/// ':' exclusion, and the 128-vs-200 length bound) before a third caller reproduced the same shape
/// checks a fourth time as a fresh inline expression. <see cref="AllowedCharset"/> is shared by three
/// of those four callers; <c>PlanRunExecutor</c>'s <c>RunId</c> check uses a narrower,
/// differently-bounded charset of its own (<c>IPlanRunExecutor.IsWellFormedAgentId</c>) for historical
/// reasons, so only the charset-independent shape checks here are shared with it — see
/// <see cref="HasUnsafeShape"/>.
/// </para>
/// </remarks>
public static class StorageSegmentSafety
{
    /// <summary>
    /// The full allowlist a tool-result scope id, conversation id, or plan run id must satisfy:
    /// 1-200 characters from <c>[A-Za-z0-9_.:-]</c>. Anchored with <c>\A</c>/<c>\z</c>, not
    /// <c>^</c>/<c>$</c> — <c>$</c> in .NET regex matches immediately before a trailing <c>'\n'</c> as
    /// well as at the true end of the string, so a caller-supplied value ending in a newline would
    /// otherwise pass (a security-review finding elsewhere in this codebase; <c>\z</c> matches only
    /// the absolute end).
    /// </summary>
    public static readonly Regex AllowedCharset = new(@"\A[A-Za-z0-9_.:-]{1,200}\z", RegexOptions.Compiled);

    /// <summary>Whether <paramref name="value"/> is exactly <c>"."</c> or <c>".."</c> — within the
    /// allowed charset but resolving to the current or parent directory once combined into a path.
    /// <see langword="null"/> is never a relative directory reference — a caller pairing this with
    /// <see cref="AllowedCharset"/> already refuses null/empty separately with a clearer message.</summary>
    public static bool IsRelativeDirectoryReference(string? value) => value is "." or "..";

    /// <summary>Whether <paramref name="value"/> ends in a dot — within the allowed charset, but
    /// Windows silently strips a trailing dot from a path segment, so two different-looking values
    /// could otherwise collide onto the same directory. Null-safe (returns <see langword="false"/> for
    /// <see langword="null"/>), matching this type's other two checks — /code-review finding: an
    /// earlier version took a non-nullable <see langword="string"/> here, which crashed with a
    /// <see cref="NullReferenceException"/> when reached through a FluentValidation <c>.Must()</c> on
    /// a property that CLR nullable-reference annotations do not stop a lenient JSON deserializer from
    /// setting to <see langword="null"/> at runtime.</summary>
    public static bool HasTrailingDot(string? value) => value is not null && value.EndsWith('.');

    /// <summary>
    /// True when <paramref name="value"/> is a relative directory reference, a rooted/absolute path,
    /// or has a trailing dot — the three charset-independent shape checks every storage-segment user
    /// must apply in addition to whichever charset it enforces.
    /// </summary>
    /// <remarks>
    /// <see cref="Path.IsPathRooted(string)"/> is intentionally platform-relative, not a fixed rule:
    /// on Windows it measures a bare drive reference like <c>"C:"</c> or <c>"C:foo"</c> as rooted; on
    /// Linux/macOS it measures only a leading <c>'/'</c> as rooted, and <c>'/'</c> is never charset-legal
    /// here in the first place, so this check is a no-op for that shape there — not a gap. A build-and-test
    /// finding on #559-563 (this codebase's Windows-run test suite hard-coded the Windows-only "C:" throws
    /// expectation, unconditionally, and failed on the Linux CI runner) is the reason this remark exists:
    /// do not "fix" this method into throwing uniformly across platforms for that shape — the drive-letter
    /// attack this check exists to close (<see cref="Path.Combine(string, string)"/> discarding every
    /// earlier segment once a value is rooted) has no equivalent on a platform without drive letters, and
    /// this check already reflects that correctly for whichever OS it runs on.
    /// </remarks>
    public static bool HasUnsafeShape(string? value) =>
        IsRelativeDirectoryReference(value) || (value is not null && Path.IsPathRooted(value)) || HasTrailingDot(value);
}
