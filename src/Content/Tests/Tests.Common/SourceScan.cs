using System.Collections.Concurrent;
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
    /// <strong>Cached — read <see cref="Cache"/>'s remarks before removing it.</strong> The tree is
    /// read once per test assembly. An earlier version of this comment argued the opposite and was
    /// wrong in a way that cost a measurably flaky suite; that history is recorded there so the
    /// argument is not made again from the same incomplete cost model.
    /// </para>
    /// </remarks>
    public static (string Path, string Code)[] ReadProductionSources(string contentRoot) =>
        Cache.GetOrAdd(contentRoot, root => Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsExcluded(f, root))
            .Select(f => (Path: f, Code: StripCommentsAndStrings(File.ReadAllText(f))))
            .ToArray());

    /// <summary>
    /// One read of the tree per test assembly. The source is immutable for the lifetime of a run, so
    /// a second read can only ever reproduce the first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Added because not caching measurably destabilized the suite.</strong> When this helper
    /// was extracted, the duplicate passes were left in deliberately, on the argument that ~3.5s of
    /// repeated work was not worth ~38 MB of stripped source held resident. That weighed the wrong
    /// cost. Widening the validator guard's candidacy took one of its passes from 27 files to all
    /// ~3,900 (19 MB), three times per run — and <c>ProcessSandboxExecutor</c>'s tests spawn real OS
    /// processes with timing-sensitive assertions that are starved under that I/O.
    /// </para>
    /// <para>
    /// Measured, same machine, back to back: the commit before that change passed the full solution
    /// 3 runs out of 3; the commit after passed 1 out of 3, with every failure in the sandbox
    /// process-executor family. The real cost of the duplicate reads was not seconds, it was whether
    /// the suite could be trusted — which is not a trade anyone would have taken knowingly.
    /// </para>
    /// <para>
    /// Keyed by root so a caller passing a different tree is not served the wrong one. Never invalidated:
    /// a test that edits production source mid-run and expects this to notice would be relying on
    /// behaviour that was never promised, and every current caller reads the tree to reason about the
    /// committed state.
    /// </para>
    /// </remarks>
    private static readonly ConcurrentDictionary<string, (string Path, string Code)[]> Cache = new(StringComparer.Ordinal);

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
