using System.Text.RegularExpressions;
using FluentAssertions;
using Tests.Common;
using Xunit;

namespace Application.Common.Tests.Helpers;

/// <summary>
/// Asserts that the governance vocabulary is parsed <strong>only</strong> by name — that no
/// production file reads one of these enums with a bare <see cref="Enum.TryParse{TEnum}(string?, bool, out TEnum)"/>
/// or <see cref="Enum.Parse{TEnum}(string)"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a source scan and not a unit test.</strong> The defect is not a wrong answer from
/// <c>EnumNameHelper</c> — it is correct, and tested. It is a <em>new call site</em> that never asks
/// it. No unit test can fail for code that was never written, so the check has to be over the source.
/// </para>
/// <para>
/// <strong>This is not hypothetical.</strong> #296 converted three sites; #300 swept fourteen more
/// and, doing so by enumeration, still missed <c>SubmitChangeProposalCommandHandler.ResolveTier</c> —
/// a near-verbatim duplicate of <c>AutonomyTierRuleProvider.ResolveTier</c>, same two enums, same
/// config key, two files away, in the very pull request whose purpose was to close these. It also
/// missed a boot validator (<c>IncidentResponsePlanValidator</c>) and an entire enum family
/// (<c>RedactionCategory</c>, where the validator and the runtime reader had already drifted into
/// disagreeing about whether <c>"2"</c> was a category). A rule enforced by whoever runs the grep
/// lasts exactly as long as the grep.
/// </para>
/// <para>
/// <strong>Why the vocabulary and not the API.</strong> Banning <c>Enum.TryParse</c> outright would
/// need a large allowlist for the legitimate round-trips — graph properties, EF rows, and JSON
/// converters reading values this system itself wrote, where the numeric form is harmless and
/// refusing it could reject data already on disk. Scoping to the enums that drive governance
/// decisions keeps the allowlist at one entry and the signal at 100%.
/// </para>
/// <para>
/// <strong>Adding an enum here is the point.</strong> A new governance enum should arrive with a
/// one-line diff to this array, which is the moment a reviewer gets to ask whether it is parsed by
/// name everywhere it is read.
/// </para>
/// </remarks>
public sealed class GovernanceEnumParseChokepointTests
{
    /// <summary>
    /// Enums whose value decides what an agent is permitted to do. Every one of these is read from
    /// configuration, a plugin manifest, an A2A envelope, or a model response — never from our own
    /// serializer — so a numeric or comma-composite form is always a typo or an attack, never data.
    /// </summary>
    private static readonly string[] GovernanceEnums =
    [
        "AutonomyLevel",
        "AutonomyDecision",
        "BlastRadius",
        "SubagentType",
        "PermissionBehaviorType",
        "ToolCapability",
        "SandboxIsolationLevel",
        "PolicyFindingSeverity",
        "AgentIdentityKind",
        "RedactionCategory",

        // MergeGate short-circuits its real apply on `Mode == Shadow`, so any value that is not
        // exactly Shadow writes for real. A numeric config value turned a dry run into production
        // writes — the sharpest consequence found anywhere in this sweep.
        "OrchestratorMode",

        // Domain-layer enums, reachable only since #312 moved the helper into Domain.Common. Both
        // were parsed by hand-rolled weaker copies of this rule before that.
        //
        // IacScanSeverity decides whether an infrastructure scan blocks a proposal. It is read from
        // two directions with opposite failure semantics: the configured blocking threshold, where
        // refusing fails closed, and each finding's severity as scraped from Checkov / tfsec /
        // ARM-TTK output, where refusing failed open until #312 gave that path its own reader.
        // Those scrapers capture `\w+`, which includes digits, so the numeric form is reachable from
        // tool output and not just from config.
        "IacScanSeverity",

        // MemoryTrust marks a fact as quarantined. Its reader falls back to Trusted — recallable —
        // so anything that makes the marker unreadable fails open.
        "MemoryTrust",

        // ApproverVerdict decides whether a consequential tool call proceeds. Read from durable
        // storage (a hand-edited or corrupted row) via ApproverDecisionJsonConverter — the exact
        // shape this guard exists for: a bare Enum.TryParse read "Deny,Approve" (0|1) as a clean
        // Approve, which Enum.IsDefined cannot distinguish from having named Approve directly.
        "ApproverVerdict"
    ];

    /// <summary>
    /// The only file permitted to parse these by any other means — the shared reader itself, which
    /// calls <c>Enum.TryParse</c> once, behind the guards.
    /// </summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "EnumNameHelper.cs"
    };

    /// <summary>
    /// Files exempt from the <em>inferred</em> matcher only, because its file-level attribution can
    /// name an enum the file merely mentions. Deliberately separate from <see cref="Allowed"/>:
    /// silencing a heuristic false positive must not also switch off <see cref="BareParseOf"/> for
    /// that file, which is the precise check the guard was built for. Empty today.
    /// </summary>
    private static readonly HashSet<string> AllowedInferred = new(StringComparer.OrdinalIgnoreCase);

    private const string SourceGlob = "*.cs";

    /// <summary>
    /// Matches a bare framework parse of one of the governance enums, in either the
    /// <c>TryParse</c> or <c>Parse</c> form, allowing whitespace around the type argument.
    /// </summary>
    private static Regex BareParseOf(string enumName)
        => new($@"\bEnum\.(TryParse|Parse)\s*<\s*{enumName}\s*>", RegexOptions.None, TimeSpan.FromSeconds(5));

    /// <summary>
    /// Matches a framework parse whose enum type is <em>inferred</em> from the <c>out</c> variable
    /// rather than written as a type argument — <c>Enum.TryParse(raw, true, out severity)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This shape was invisible to the guard until #312, and it is the shape the offender
    /// used.</strong> <c>IacScanSeverityParser</c> parsed a severity that decides whether an
    /// infrastructure scan blocks a proposal, with two of the four guards and no type argument, so
    /// <see cref="BareParseOf"/> could not see it however many enums the array listed. Adding an
    /// enum name to a matcher that cannot match the call shape is a test that only looks like a
    /// control — which is the failure this whole family of guards exists to prevent.
    /// </para>
    /// <para>
    /// The call text alone cannot say which enum an inferred parse targets, so a file is an offender
    /// when it contains one <em>and</em> names a governance enum. That is a heuristic and could flag
    /// a file that parses something else while merely mentioning a governance enum; measured across
    /// production source it flags nothing beyond the two files that should be flagged, and the
    /// existing one-entry allowlist absorbs the helper itself. A future false positive is a
    /// reviewer's decision, which the guard's design already treats as the point.
    /// </para>
    /// </remarks>
    private static readonly Regex InferredParse =
        new(@"\bEnum\.(TryParse|Parse)\s*\(", RegexOptions.None, TimeSpan.FromSeconds(5));

    [Fact]
    public void GovernanceEnumsAreParsedOnlyThroughEnumNameHelper()
    {
        var contentRoot = Path.Combine(RepoRoot.Path, "src", "Content");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(contentRoot, SourceGlob, SearchOption.AllDirectories))
        {
            if (IsExcluded(file) || Allowed.Contains(Path.GetFileName(file)))
                continue;

            var code = StripCommentsAndStrings(File.ReadAllText(file));

            var named = GovernanceEnums.Where(e => BareParseOf(e).IsMatch(code)).ToArray();

            // An inferred parse names no type, so attribute it to whichever governance enums the
            // file mentions — see the remarks on InferredParse for why that heuristic is the only
            // thing a source scan can do here, and what it measured.
            var inferred = InferredParse.IsMatch(code) && !AllowedInferred.Contains(Path.GetFileName(file))
                ? GovernanceEnums.Where(e => Regex.IsMatch(code, $@"\b{e}\b")).ToArray()
                : [];

            var offending = named.Union(inferred, StringComparer.Ordinal).ToArray();
            if (offending.Length > 0)
                offenders.Add($"{Path.GetRelativePath(contentRoot, file)} → {string.Join(", ", offending)}");
        }

        offenders.Should().BeEmpty(
            "Enum.TryParse returns true for ANY integer string, including one outside the defined "
            + "range, and hands back a value that compiles, compares, and ToString()s while not being "
            + "a member. On a governance enum that silently changes a safety decision: a severity "
            + "threshold that can never be met, a tier looser than the loosest real tier, a capability "
            + "grant with every bit set. Use EnumNameHelper.TryParseName instead. Offenders:\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void TheGuardWouldActuallyFire()
    {
        // The scan above passes trivially if the matching is broken — an empty offender list is the
        // same shape whether nothing violates the rule or nothing is being read. This proves the
        // matcher recognises the real shapes, and that the doc comments this very sweep added (which
        // quote Enum.TryParse<ToolCapability>("255", …) verbatim) are not counted as violations.
        var tryParse = StripCommentsAndStrings(
            "if (Enum.TryParse<AutonomyLevel>(raw, ignoreCase: true, out var t)) { }");
        var parse = StripCommentsAndStrings("var t = Enum.Parse<BlastRadius>(raw);");
        var spaced = StripCommentsAndStrings("Enum.TryParse < ToolCapability > (raw, out var c);");
        var comment = StripCommentsAndStrings(
            "// a bare Enum.TryParse<AutonomyLevel> accepts any integer string\npublic class X { }");
        var otherEnum = StripCommentsAndStrings("Enum.TryParse<StepExecutionStatus>(raw, out var s);");

        BareParseOf("AutonomyLevel").IsMatch(tryParse).Should().BeTrue();
        BareParseOf("BlastRadius").IsMatch(parse).Should().BeTrue();
        BareParseOf("ToolCapability").IsMatch(spaced).Should().BeTrue();

        BareParseOf("AutonomyLevel").IsMatch(comment).Should().BeFalse(
            "rationale comments naming the trap must not be violations, or the sweep's own "
            + "documentation would trip the guard it added");

        GovernanceEnums.Any(e => BareParseOf(e).IsMatch(otherEnum)).Should().BeFalse(
            "round-trips of values this system wrote itself are deliberately out of scope");
    }

    /// <summary>
    /// The inferred form, which the guard could not see until #312. This is the control for that
    /// gap: the exact source line <c>IacScanSeverityParser</c> carried, which listed
    /// <c>IacScanSeverity</c> in the array above and still went unreported, because the matcher
    /// required a type argument the call does not have.
    /// </summary>
    [Fact]
    public void TheGuardCatchesAParseWhoseEnumTypeIsInferred()
    {
        var theOffendingLine = StripCommentsAndStrings(
            "public static bool TryParse(string? value, out IacScanSeverity severity)"
            + " => Enum.TryParse(value?.Trim(), ignoreCase: true, out severity) && Enum.IsDefined(severity);");

        InferredParse.IsMatch(theOffendingLine).Should().BeTrue(
            "an inferred parse is the same defect wearing different syntax");
        GovernanceEnums.Any(e => BareParseOf(e).IsMatch(theOffendingLine)).Should().BeFalse(
            "and the type-argument matcher alone genuinely cannot see it — that is why this exists");

        // The explicit form must not be double-reported by the inferred matcher's own call paren.
        InferredParse.IsMatch(StripCommentsAndStrings("Enum.TryParse<AutonomyLevel>(raw, out var t);"))
            .Should().BeFalse("a type argument means the enum is named, and BareParseOf owns that case");

        InferredParse.IsMatch(StripCommentsAndStrings("var ok = int.TryParse(raw, out var n);"))
            .Should().BeFalse("only Enum.TryParse / Enum.Parse are in scope");
    }

    [Fact]
    public void TheScanReadsARepresentativeNumberOfFiles()
    {
        // Guards the other direction: a wrong root or a broken glob makes the scan pass by reading
        // nothing at all. The floor is far below the real count so ordinary churn cannot trip it.
        var contentRoot = Path.Combine(RepoRoot.Path, "src", "Content");

        Directory.EnumerateFiles(contentRoot, SourceGlob, SearchOption.AllDirectories)
            .Count(f => !IsExcluded(f))
            .Should().BeGreaterThan(500);
    }

    [Fact]
    public void EveryGuardedEnumStillExists()
    {
        // A renamed or deleted enum would silently stop being guarded — the scan would keep passing
        // because nothing matches a name nothing uses. Anchoring on the declaration keeps the array
        // honest.
        var contentRoot = Path.Combine(RepoRoot.Path, "src", "Content");
        var declarations = Directory
            .EnumerateFiles(Path.Combine(contentRoot, "Domain"), SourceGlob, SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(contentRoot, "Application"), SourceGlob, SearchOption.AllDirectories))
            .Where(f => !IsExcluded(f))
            .Select(File.ReadAllText)
            .ToArray();

        var missing = GovernanceEnums
            .Where(e => !declarations.Any(src => Regex.IsMatch(src, $@"\benum\s+{e}\b")))
            .ToArray();

        missing.Should().BeEmpty(
            "a guarded enum that no longer exists under this name is a guard that silently stopped "
            + "guarding. Missing: " + string.Join(", ", missing));
    }

    /// <summary>Skips test code and build output — the rule is about production call sites.</summary>
    private static bool IsExcluded(string path)
    {
        var relative = Path.GetRelativePath(Path.Combine(RepoRoot.Path, "src", "Content"), path);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Contains("Tests", StringComparer.OrdinalIgnoreCase)
            || segments.Contains("bin", StringComparer.OrdinalIgnoreCase)
            || segments.Contains("obj", StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Removes comments and string literals so only compiled code is matched.
    /// </summary>
    /// <remarks>
    /// Deliberately crude — it does not parse C#. It only has to be conservative in the direction
    /// that matters: a construct it mishandles yields a false <em>positive</em>, which surfaces as a
    /// failing test naming the file, not a silently missed call site.
    /// </remarks>
    private static string StripCommentsAndStrings(string source)
    {
        var withoutBlockComments = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        var withoutLineComments = Regex.Replace(withoutBlockComments, @"//[^\n]*", " ");
        return Regex.Replace(withoutLineComments, "\"(?:[^\"\\\\\n]|\\\\.)*\"", "\"\"");
    }
}
