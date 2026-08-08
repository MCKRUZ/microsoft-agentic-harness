using System.Text.RegularExpressions;
using Tests.Common;
using Xunit;

namespace Infrastructure.Observability.Tests.Schema;

/// <summary>
/// Binds the dashboard's <c>SessionStatus</c> union to the C# enum — the last of the five places this
/// vocabulary is written down, and the one that was still relying on a comment.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the TypeScript compiler is not enough, despite appearances.</strong> The badge's style
/// map is keyed by the union, so a value in the union with no style is a build error. That is real,
/// and it is also not the failure mode. It catches the union drifting from the map <em>inside</em>
/// TypeScript; nothing catches the union drifting from the C# enum that produces the values. Add a
/// status and the enum, the migration and both Grafana dashboards go red while the frontend compiles
/// perfectly and renders the badge with no colour — the exact symptom this branch just finished
/// fixing in Grafana, one layer over.
/// </para>
/// <para>
/// A previous version of the sibling dashboard test asserted the compiler had this covered. It did
/// not, and saying so was worse than saying nothing, because it is the kind of claim that stops
/// anyone checking.
/// </para>
/// <para>
/// Reading the union out of the file rather than generating it: the file also carries a colour per
/// status, and colour is human judgement — the badge argues in its own comments why cancelled is
/// slate rather than amber. Generating would turn that judgement into a codegen merge and fight
/// Grafana-style edit-and-export workflows for no extra guarantee. A red test on drift buys the same
/// thing.
/// </para>
/// </remarks>
public sealed class SessionStatusFrontendAgreementTests
{
    private static readonly string BadgeFile = RepoRoot.Combine(
        "src", "Content", "Presentation", "Presentation.Dashboard",
        "src", "routes", "Sessions", "StatusBadge.tsx");

    /// <summary>
    /// Matches the exported union and captures everything between the <c>=</c> and the <c>;</c>.
    /// </summary>
    private static readonly Regex UnionDeclaration = new(
        @"export\s+type\s+SessionStatus\s*=\s*(?<values>[^;]+);",
        RegexOptions.Singleline);

    [Fact]
    public void TheDashboardsStatusUnion_MatchesTheStatusesTheCodeCanWrite()
    {
        var match = UnionDeclaration.Match(File.ReadAllText(BadgeFile));

        // The reader's own guard. A rename or reformat that stops this matching must fail loudly
        // rather than compare the enum against an empty set and pass.
        Assert.True(match.Success,
            $"No 'export type SessionStatus = ...' declaration found in {BadgeFile}. If the union " +
            "moved or was renamed, repoint this test — do not delete it; nothing else binds the " +
            "frontend vocabulary to the C# enum.");

        var declared = match.Groups["values"].Value
            .Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(v => v.Trim('\'', '"'))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            SessionStatusVocabulary.Writable,
            SessionStatusVocabulary.Ordered(declared));
    }
}
