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
    /// Every production source file under <paramref name="contentRoot"/>, comment- and
    /// string-stripped, as (path, code).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The enumerate + <see cref="IsExcluded"/> + <see cref="StripCommentsAndStrings"/> triple is the
    /// predicate that decides <strong>which files a guard test can see at all</strong>. That is
    /// exactly the thing that must not drift between guards: a scan which silently narrows reports
    /// nothing and looks identical to a scan which found nothing. It had reached three verbatim
    /// copies inside <c>SecurityControlHasACallerTests</c> alone, which is the same threshold at
    /// which this class was itself extracted.
    /// </para>
    /// <para>
    /// <strong>Deliberately not cached.</strong> Each call re-reads the tree — about 3,900 files and
    /// 19 MB, roughly two seconds. Memoizing it would save a few seconds per test class but hold
    /// ~38 MB of stripped UTF-16 source resident for the lifetime of the test assembly, where today
    /// each pass is collectable as soon as its fact ends. Inside a suite that runs for minutes that
    /// trade is not worth making silently; if a future caller needs it, add the cache here with a
    /// measurement rather than in one caller.
    /// </para>
    /// </remarks>
    public static (string Path, string Code)[] ReadProductionSources(string contentRoot)
    {
        return Directory
            .EnumerateFiles(contentRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsExcluded(f, contentRoot))
            .Select(f => (Path: f, Code: StripCommentsAndStrings(File.ReadAllText(f))))
            .ToArray();
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
