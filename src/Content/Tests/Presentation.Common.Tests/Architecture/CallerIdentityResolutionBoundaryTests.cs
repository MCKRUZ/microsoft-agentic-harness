using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Presentation.Common.Tests.Architecture;

/// <summary>
/// Architecture guard: caller identity may be resolved in exactly ONE place. Source-scans the
/// production tree and fails when any other file resolves identity from claims by hand.
/// </summary>
/// <remarks>
/// <para>
/// This exists because a second precedence ladder has caused three separate world-readable-record
/// defects in this repo, and the fix for all three still relied on every future author remembering to
/// call the shared extension. Nothing structural stopped a fourth. When this test was written it
/// immediately found two live drift sites — <c>AgUiRunHandler.GetCallerId</c>, whose comment claimed it
/// "mirrors" the shared helper while being a stale copy that rejected <c>sub</c>, and
/// <c>McpController</c>, which logged an oid-only Entra caller as "anonymous" in the audit trail.
/// </para>
/// <para>
/// The pattern is anchored on <em>identity</em> claim literals appearing in a <em>lookup</em>
/// expression. That is deliberate and is what keeps it from becoming the kind of brittle check people
/// learn to suppress: role and scope claims are untouched, minting a claim
/// (<c>new Claim("oid", …)</c>) in a dev/test auth handler is untouched, and resolution driven by a
/// configured claim-type variable through <c>ApproverClaimTypes.EquivalentFormsOf</c> is untouched
/// because it carries no literal.
/// </para>
/// </remarks>
public sealed class CallerIdentityResolutionBoundaryTests
{
    /// <summary>Claim literals that denote a caller's identity. Roles and scopes are deliberately absent.</summary>
    private static readonly string[] IdentityClaimLiterals =
    [
        "\"oid\"",
        "\"sub\"",
        "\"upn\"",
        "ClaimTypes.NameIdentifier",
        "ClaimTypes.Upn",
        "objectidentifier",
        "claims/nameidentifier",
    ];

    /// <summary>Claim <em>lookup</em> APIs. Construction (<c>new Claim</c>) is intentionally not here.</summary>
    private static readonly Regex LookupApi = new(
        @"\b(FindFirst|FindFirstValue|FindAll|HasClaim)\s*\(|\bClaims\s*\.\s*(Where|Any|First|FirstOrDefault|Single|SingleOrDefault|Select)",
        RegexOptions.Compiled);

    /// <summary>
    /// Files permitted to resolve identity claims directly, each with the reason it is not a violation.
    /// Add to this list — with a reason — rather than widening the pattern or deleting the check.
    /// </summary>
    /// <remarks>
    /// Matched on the full relative path, not the bare file name, so a second file that merely reuses an
    /// exempt name does not inherit its exemption — a copied-and-renamed resolver is precisely the drift
    /// this guard exists to catch.
    /// </remarks>
    private static readonly (string RelativePath, string Reason)[] Exemptions =
    [
        ("Presentation/Presentation.Common/Extensions/ClaimsPrincipalExtensions.cs",
            "THE single authority. This is the one ladder every other caller must route through."),

        ("Infrastructure/Infrastructure.AI/Governance/CapabilityEnvelopeResolver.cs",
            "Resolves a DIFFERENT subject on purpose: the capability-envelope grant key, which is " +
            "NameIdentifier then sub and deliberately NOT oid. Aligning it with caller identity would " +
            "make the anonymous dev principal addressable by an Envelopes:BySubject grant and turn a " +
            "fail-closed default into something an operator could widen by name. See the remarks on " +
            "AnonymousAuthenticationHandler."),
    ];

    [Fact]
    public void CallerIdentity_IsResolvedOnlyByTheSingleAuthority()
    {
        var sourceRoot = FindProductionSourceRoot();
        var files = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsGeneratedOrBuildOutput(path))
            .ToList();

        // Guard against the test silently passing because it scanned nothing — a vacuous architecture
        // test is worse than none, because it reads as protection.
        files.Should().HaveCountGreaterThan(200,
            "the scan must actually cover the production tree; a near-empty scan means the root lookup broke");

        var violations = files
            .SelectMany(FindHandRolledIdentityLookups)
            .ToList();

        // Assert.Fail rather than Should().BeEmpty(): the guidance below is the entire value of this
        // test, and an assertion-library object dump appended after it only buries the instructions.
        if (violations.Count > 0)
            Assert.Fail(BuildFailureMessage(violations));
    }

    // -- Detection --

    private static IEnumerable<Violation> FindHandRolledIdentityLookups(string path)
    {
        var normalized = path.Replace(Path.DirectorySeparatorChar, '/');
        if (Exemptions.Any(e => normalized.EndsWith("/" + e.RelativePath, StringComparison.Ordinal)))
            yield break;

        var lines = File.ReadAllLines(path);

        for (var i = 0; i < lines.Length; i++)
        {
            var code = StripComment(lines[i]);
            if (!LookupApi.IsMatch(code))
                continue;

            // Join a small window so a fluent lookup wrapped across lines is still seen whole, e.g.
            //     principal
            //         .FindFirstValue("oid");
            var window = string.Concat(
                code,
                " ",
                i + 1 < lines.Length ? StripComment(lines[i + 1]) : string.Empty,
                " ",
                i + 2 < lines.Length ? StripComment(lines[i + 2]) : string.Empty);

            var literal = IdentityClaimLiterals.FirstOrDefault(
                l => window.Contains(l, StringComparison.Ordinal));

            if (literal is not null)
                yield return new Violation(path, i + 1, lines[i].Trim(), literal);
        }
    }

    /// <summary>
    /// Removes line and XML-doc comments so prose mentioning a claim name never trips the scan,
    /// while leaving string literals intact.
    /// </summary>
    /// <remarks>
    /// The string tracking is not incidental rigour — it is the difference between this guard working
    /// and quietly not working. The mapped-token claim types this guard exists to catch are URIs
    /// (<c>http://schemas.microsoft.com/identity/claims/objectidentifier</c>), and a naive
    /// "truncate at the first //" would cut such a line down to <c>…FindFirstValue("http:</c> — losing
    /// the very literal being searched for and passing a hand-rolled resolver as clean. So a "//"
    /// only ends the line when it appears outside a string or character literal.
    /// </remarks>
    private static string StripComment(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("///", StringComparison.Ordinal)
            || trimmed.StartsWith("*", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var inString = false;
        var verbatim = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (inString)
            {
                if (verbatim)
                {
                    // Inside @"…", the only escape is a doubled quote.
                    if (c != '"')
                        continue;
                    if (i + 1 < line.Length && line[i + 1] == '"')
                        i++;
                    else
                        inString = false;
                }
                else if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    verbatim = i > 0 && line[i - 1] == '@';
                    break;

                // Skip a char literal wholesale so '"' cannot be mistaken for a string opener.
                case '\'':
                    i += i + 1 < line.Length && line[i + 1] == '\\' ? 3 : 2;
                    break;

                case '/' when i + 1 < line.Length && line[i + 1] == '/':
                    return line[..i];
            }
        }

        return line;
    }

    [Theory]
    // The regression this guard nearly shipped with: a claim-type URI must survive stripping.
    [InlineData(
        """principal.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier");""",
        """principal.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier");""")]
    [InlineData("""var x = @"http://example/claims/nameidentifier";""", """var x = @"http://example/claims/nameidentifier";""")]
    // A real trailing comment still gets cut, including one that follows a URI literal.
    [InlineData("""var x = "oid"; // resolves the caller""", """var x = "oid"; """)]
    [InlineData("""var u = "http://a/b"; // trailing""", """var u = "http://a/b"; """)]
    // A quote inside a char literal must not open a string and swallow the rest of the line.
    [InlineData("""var q = '"'; // note""", """var q = '"'; """)]
    // Doc and block-comment continuation lines contribute nothing.
    [InlineData("""    /// Mentions "oid" in prose.""", "")]
    [InlineData("""     * Mentions "oid" in prose.""", "")]
    public void StripComment_KeepsStringLiterals_AndCutsOnlyRealComments(string line, string expected)
        => StripComment(line).Should().Be(expected);

    private static bool IsGeneratedOrBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.EndsWith(".g.cs", StringComparison.Ordinal);

    /// <summary>
    /// Walks up from the test binary to the repo, then returns <c>src/Content</c>. Fails loudly rather
    /// than skipping, so a moved directory cannot quietly disarm the guard.
    /// </summary>
    private static string FindProductionSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Content");
            if (Directory.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate 'src/Content' walking up from {AppContext.BaseDirectory}. This guard " +
            "cannot run; fix the lookup rather than deleting the test.");
    }

    // -- Reporting: the message IS the value of this test --

    private static string BuildFailureMessage(IReadOnlyCollection<Violation> violations)
    {
        if (violations.Count == 0)
            return string.Empty;

        var message = new StringBuilder()
            .AppendLine()
            .AppendLine("Caller identity must be resolved ONLY through ClaimsPrincipalExtensions")
            .AppendLine("(src/Content/Presentation/Presentation.Common/Extensions/ClaimsPrincipalExtensions.cs).")
            .AppendLine()
            .AppendLine("WHY THIS IS GUARDED: a second precedence ladder has caused THREE separate")
            .AppendLine("world-readable-record defects in this repo. The mechanism is the same every time —")
            .AppendLine("one resolver accepts a token shape another rejects, identity resolves to null, and a")
            .AppendLine("null owner is treated as GLOBAL (not private) by PlannerScopeFilter.VisibleTo and")
            .AppendLine("TenantIsolatedGraphStore. The record becomes readable by every caller in every tenant.")
            .AppendLine()
            .AppendLine("WHAT TO DO INSTEAD:")
            .AppendLine("  principal.GetUserIdOrNull()  // null when absent OR ambiguous -> reject the caller")
            .AppendLine("  principal.GetUserId()        // throws; for [Authorize]-protected paths")
            .AppendLine("  principal.GetTenantId()")
            .AppendLine()
            .AppendLine("IF YOUR CASE GENUINELY DIFFERS (you are resolving some OTHER subject, not the")
            .AppendLine("caller's identity), add the file to Exemptions in this test WITH a written reason.")
            .AppendLine("Do not widen the pattern, and do not delete the check.")
            .AppendLine()
            .AppendLine("Hand-rolled identity lookups found:");

        foreach (var violation in violations)
        {
            message.AppendLine(
                $"  {violation.RelativePath}:{violation.Line}  [{violation.Literal}]  {violation.Code}");
        }

        return message.ToString();
    }

    private sealed record Violation(string Path, int Line, string Code, string Literal)
    {
        public string RelativePath
        {
            get
            {
                var marker = $"src{System.IO.Path.DirectorySeparatorChar}Content";
                var index = Path.IndexOf(marker, StringComparison.Ordinal);
                return index >= 0 ? Path[index..] : Path;
            }
        }
    }
}
