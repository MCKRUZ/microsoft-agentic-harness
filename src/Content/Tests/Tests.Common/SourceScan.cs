using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
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
    /// <para>
    /// <strong>Read-only because the instance is shared.</strong> Every caller in a run receives the
    /// same object, so one caller sorting or assigning into it in place would silently change what
    /// every other guard sees — and a guard reading a mutated view of the source tree passes
    /// vacuously, which is the single failure mode this whole class exists to prevent.
    /// </para>
    /// <para>
    /// The wrap is done <strong>once, at the cache boundary</strong>, and the cache stores the wrapper
    /// rather than the array. Declaring the return type as <see cref="IReadOnlyList{T}"/> while still
    /// handing back the array would be only a cast: a caller could cast it back and mutate the shared
    /// instance, and the paragraph above would be asserting an enforcement the code did not provide —
    /// which is precisely the defect this class is built to catch, committed in its own source. The
    /// backing array is never reachable after construction, so the guarantee is structural rather than
    /// a convention the current callers happen to honour.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<(string Path, string Code)> ReadProductionSources(string contentRoot) =>
        Cache.GetOrAdd(contentRoot, root => new ReadOnlyCollection<(string Path, string Code)>(Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsExcluded(f, root))
            .Select(f => (Path: f, Code: StripCommentsAndStrings(File.ReadAllText(f))))
            .ToArray()));

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
    private static readonly ConcurrentDictionary<string, ReadOnlyCollection<(string Path, string Code)>> Cache =
        new(StringComparer.Ordinal);

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

    /// <summary>
    /// Every <c>class</c>/<c>record</c>/<c>struct</c> declared in <paramref name="code"/>, as
    /// (type name, base list).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Extracted for #534, shared by three consumers in <c>SecurityControlHasACallerTests</c>
    /// (<c>Implements</c>, <c>FindMediatRRequestTypes</c>, <c>FindValidatorDeclarations</c>) that had
    /// each written their own answer to the same question — "what is this type's base list" — at
    /// different fidelity. The base list is cut at <c>where</c> so a generic constraint
    /// (<c>where T : IFoo</c>) is never read as a base type.
    /// </para>
    /// <para>
    /// <strong>The primary-constructor group must be nesting-aware.</strong> A first cut used
    /// <c>(?:\([^)]*\))?</c> — a flat, non-nesting match. A tuple-typed parameter
    /// (<c>record Foo((int, int) Point)</c>) or a default value that calls another method
    /// (<c>record Foo(int X = Math.Max(1, 2))</c>) both put a second <c>(...)</c> inside the outer
    /// one; <c>[^)]*</c> stops at the FIRST inner <c>)</c>, leaves the real closing paren unconsumed,
    /// and the whole declaration fails to match — silently dropping the type from every consumer's
    /// view, not just misparsing its parameter list. The balancing-group construct below
    /// (<c>(?&lt;pcDepth&gt;</c>/<c>(?&lt;-pcDepth&gt;</c>/<c>(?(pcDepth)(?!))</c>) matches parens at
    /// arbitrary nesting depth instead of assuming exactly one level.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<(string Name, string BaseList)> FindTypeDeclarations(string code) =>
        TypeDeclarationCache.GetValue(code, ParseTypeDeclarations);

    /// <summary>
    /// Cached by reference identity, not content — every caller in a run reuses the same
    /// <see cref="ReadProductionSources"/> tuples, so a <see cref="ConditionalWeakTable{TKey,TValue}"/>
    /// avoids both re-parsing a file's declarations once per contract checked against it (#534) and
    /// re-hashing multi-KB source strings on every lookup, which a content-keyed
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/> would not avoid — string hash codes are not
    /// cached across calls in .NET Core. Never invalidated, matching <see cref="Cache"/>'s own
    /// reasoning: the source this reads is the committed state for the lifetime of a test run.
    /// </summary>
    private static readonly ConditionalWeakTable<string, IReadOnlyList<(string Name, string BaseList)>>
        TypeDeclarationCache = new();

    private static IReadOnlyList<(string Name, string BaseList)> ParseTypeDeclarations(string code)
    {
        var declarations = new List<(string, string)>();

        foreach (Match declaration in Regex.Matches(
            code,
            @"\b(?:class|record|struct)\s+(\w+)\s*(?:<[^>]*>)?"
            + @"\s*(?:\((?:[^()]|(?<pcDepth>\()|(?<-pcDepth>\)))*(?(pcDepth)(?!))\))?"
            + @"\s*:\s*([^{;]*)"))
        {
            // Constraints are not base types. Everything from `where` onward describes what a type
            // parameter must satisfy, not what this type derives from.
            var baseList = Regex.Split(declaration.Groups[2].Value, @"\bwhere\b")[0];
            declarations.Add((declaration.Groups[1].Value, baseList));
        }

        return declarations;
    }
}
