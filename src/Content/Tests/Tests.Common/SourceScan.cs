using System.Text.RegularExpressions;

namespace Tests.Common;

/// <summary>
/// Shared helpers for tests that scan the repository's own compiled source — "is a security control
/// actually called," "is a chokepoint actually the only path" — so that scan logic exists once
/// rather than drifting across each guard test file that needs it.
/// </summary>
/// <remarks>
/// Extracted from three near-identical copies (<c>SecurityControlHasACallerTests</c>,
/// <c>ToolCallAdmissionChokepointTests</c>, <c>GovernanceEnumParseChokepointTests</c>) when a fourth
/// guard test (<c>ForeignTextScanningCoverageTests</c>, issue #331) was about to become a fourth
/// copy. Deliberately crude on comment/string stripping: the classifiers' own doc comments note that
/// a mishandled construct yields a false <em>positive</em> — a named failing file to review — never a
/// silent miss.
/// </remarks>
public static class SourceScan
{
    /// <summary>
    /// Whether <paramref name="path"/>, relative to <paramref name="contentRoot"/>, sits under a
    /// <c>Tests</c>, <c>bin</c>, or <c>obj</c> segment and should be excluded from a production-only
    /// source scan.
    /// </summary>
    public static bool IsExcluded(string path, string contentRoot)
    {
        var relative = Path.GetRelativePath(contentRoot, path);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Contains("Tests", StringComparer.OrdinalIgnoreCase)
            || segments.Contains("bin", StringComparer.OrdinalIgnoreCase)
            || segments.Contains("obj", StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Removes comments and string literals so only compiled code is matched — a doc comment naming
    /// a contract, or a string literal that happens to contain a matched token, must not count as a
    /// live reference.
    /// </summary>
    public static string StripCommentsAndStrings(string source)
    {
        var withoutBlockComments = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        var withoutLineComments = Regex.Replace(withoutBlockComments, @"//[^\n]*", " ");
        return Regex.Replace(withoutLineComments, "\"(?:[^\"\\\\\n]|\\\\.)*\"", "\"\"");
    }
}
