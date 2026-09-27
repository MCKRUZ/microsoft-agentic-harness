using FluentAssertions;
using Tests.Common;
using Xunit;

namespace Presentation.Common.Tests.Architecture;

/// <summary>
/// Architecture guard: <c>ToolPermissionRule.PublishedToolName</c> may be READ only where it is
/// allowed to be — the prompt summary. Source-scans the production tree and fails when any other
/// file reads it.
/// </summary>
/// <remarks>
/// <para>
/// The field exists so a summary can tell that two rules describe one logical tool and report it
/// once, under the name the agent actually invokes (#652). It must never take part in a matching or
/// authorization decision. The providers deliberately emit one rule per name-form so enforcement
/// covers a first-party tool whose DI registration key disagrees with its published name (#612,
/// #626); a matcher that preferred this field would collapse that two-name coverage back to one
/// name and reopen the gap those issues closed.
/// </para>
/// <para>
/// <strong>Why a test and not just the XML docs.</strong> The field is non-null <em>precisely</em>
/// for the security-sensitive divergent pairs, and the summary's own
/// <c>rule.PublishedToolName ?? rule.ToolPattern</c> is exactly the expression a future matcher
/// would copy — it looks like a helpful "get the real name" idiom. This repo's own history says
/// prose is the weakest available guard: CLAUDE.md's Common Mistakes records a shared field
/// silently meaning two different things landing twice, and
/// <c>SecurityControlHasACallerTests</c> exists because a control with no caller passes every test
/// around it. So the invariant is enforced by something that fails.
/// </para>
/// </remarks>
public sealed class PublishedToolNameIsPresentationOnlyTests
{
    private const string Member = "PublishedToolName";

    /// <summary>
    /// Files permitted to touch the member, each with the reason it is not a violation. Add here —
    /// with a reason — rather than deleting the check. Matched on the full relative path, so a
    /// copied-and-renamed file does not inherit an exemption.
    /// </summary>
    private static readonly (string RelativePath, string Reason)[] Exemptions =
    [
        ("Domain/Domain.AI/Permissions/ToolPermissionRule.cs",
            "Declares the field."),

        ("Application/Application.Core/Permissions/EnvelopePermissionRuleProvider.cs",
            "WRITES it at emission — the only point the name-form pairing is still known."),

        ("Application/Application.Core/Permissions/PluginPermissionRuleProvider.cs",
            "WRITES it at emission, same reason."),

        ("Infrastructure/Infrastructure.AI/Prompts/Sections/PermissionRulesSectionProvider.cs",
            "THE single reader. Groups the summary's lines by it; renders text and decides nothing."),
    ];

    [Fact]
    public void PublishedToolName_IsReadOnlyByThePromptSummary()
    {
        var sources = SourceScan.ReadProductionSources(
            Path.Combine(RepoRoot.Path, "src", "Content"));

        // A vacuous architecture test is worse than none, because it reads as protection.
        sources.Should().HaveCountGreaterThan(200,
            "the scan must cover the production tree; a near-empty scan means the root lookup broke");

        var violations = sources
            .Where(s => s.Code.Contains(Member, StringComparison.Ordinal))
            .Select(s => s.Path.Replace(Path.DirectorySeparatorChar, '/'))
            .Where(path => !Exemptions.Any(e => path.EndsWith("/" + e.RelativePath, StringComparison.Ordinal)))
            .ToList();

        // The guidance is the entire value here; an assertion-library object dump would bury it.
        if (violations.Count > 0)
        {
            Assert.Fail(
                $"{Member} is presentation-only and was referenced outside the files allowed to touch it:" +
                Environment.NewLine +
                string.Join(Environment.NewLine, violations.Select(v => "  " + v)) +
                Environment.NewLine + Environment.NewLine +
                "It records that two permission rules describe ONE logical tool, so a summary can say so " +
                "once. It must not influence matching or authorization: the rule providers emit one rule " +
                "per name-form precisely so enforcement covers a tool under BOTH its DI registration key " +
                "and its self-reported published name (#612, #626). A matcher preferring this field would " +
                "collapse that coverage to one name and reopen the gap." +
                Environment.NewLine + Environment.NewLine +
                "To match on a tool's name, use ToolPattern. If a new presentation-only consumer genuinely " +
                "needs to read this, add it to Exemptions above with the reason.");
        }
    }

    /// <summary>
    /// The guard is only meaningful if the member it names still exists — a rename would otherwise
    /// leave it scanning for a string nothing produces and passing forever.
    /// </summary>
    [Fact]
    public void TheGuardedMember_StillExists()
    {
        typeof(Domain.AI.Permissions.ToolPermissionRule)
            .GetProperty(Member)
            .Should().NotBeNull(
                $"this guard scans for the literal \"{Member}\"; if the property is renamed the scan " +
                "silently protects nothing, so rename it here too");
    }
}
