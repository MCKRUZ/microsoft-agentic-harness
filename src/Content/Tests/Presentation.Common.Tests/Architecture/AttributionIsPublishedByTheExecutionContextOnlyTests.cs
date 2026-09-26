using Application.AI.Common.Interfaces.Telemetry;
using FluentAssertions;
using Tests.Common;
using Xunit;

namespace Presentation.Common.Tests.Architecture;

/// <summary>
/// Architecture guard: external governance attribution (<see cref="IAgentTelemetryAttribution"/>) is
/// published by exactly one thing — <c>AgentExecutionContext.Initialize</c>. Source-scans the
/// production tree and fails when anything else begins a turn's attribution.
/// </summary>
/// <remarks>
/// <para>
/// This replaced a convention, and it exists because the convention failed. Attribution used to be a
/// second call each site made next to <c>IAgentExecutionContext.Initialize</c>, and of the five places
/// that establish an execution context only two made it (#737) — plan runs, sub-plans and direct tool
/// invocations exported spans with no agent identity, which a governance platform discards
/// <em>without reporting an error</em>. A tenant's agent inventory was missing whole categories of
/// activity and nothing anywhere said so.
/// </para>
/// <para>
/// <strong>Why a test and not just the XML docs.</strong> Two distinct regressions are possible and
/// neither produces a symptom. Re-adding a per-site <c>BeginTurn</c> would publish a second, identical
/// scope nested inside the context's own, and releasing the inner one restores baggage the outer turn
/// is still relying on — attribution silently disappears part-way through a turn. And a new call site
/// that publishes attribution itself <em>instead of</em> initializing a context would re-create the
/// original defect in a new place. Prose cannot catch either; this can.
/// </para>
/// <para>
/// Modelled on <see cref="PublishedToolNameIsPresentationOnlyTests"/>, which guards a comparable
/// "only these files may touch this" invariant.
/// </para>
/// </remarks>
public sealed class AttributionIsPublishedByTheExecutionContextOnlyTests
{
    private const string Member = "BeginTurn";

    /// <summary>
    /// Files permitted to name the member, each with the reason it is not a violation. Add here — with
    /// a reason — rather than deleting the check. Matched on the full relative path, so a
    /// copied-and-renamed file does not inherit an exemption.
    /// </summary>
    private static readonly (string RelativePath, string Reason)[] Exemptions =
    [
        ("Application/Application.AI.Common/Interfaces/Telemetry/IAgentTelemetryAttribution.cs",
            "Declares the member."),

        ("Application/Application.AI.Common/Services/Telemetry/NoOpAgentTelemetryAttribution.cs",
            "Implements it — the benign default that publishes nothing."),

        ("Infrastructure/Infrastructure.Observability/Agent365/Agent365TelemetryAttribution.cs",
            "Implements it for Microsoft Agent 365."),

        ("Application/Application.AI.Common/Services/Agent/AgentExecutionContext.cs",
            "THE single caller. Publishes on Initialize and releases on disposal, which is what makes "
            + "attribution automatic at every call site instead of a per-site ritual (#737)."),
    ];

    [Fact]
    public void BeginTurn_IsCalledOnlyByTheExecutionContext()
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
                $"{Member} was named outside the files allowed to publish turn attribution:" +
                Environment.NewLine +
                string.Join(Environment.NewLine, violations.Select(v => "  " + v)) +
                Environment.NewLine + Environment.NewLine +
                "Turn attribution is published by AgentExecutionContext.Initialize and released when the " +
                "DI scope owning that context is disposed. A call site does not publish it — calling " +
                "IAgentExecutionContext.Initialize is the whole of what a site has to do." +
                Environment.NewLine + Environment.NewLine +
                "Publishing a second scope alongside the context's own breaks the turn it was meant to " +
                "help: releasing the inner scope restores baggage the still-running outer turn depends " +
                "on, so attribution vanishes part-way through, silently. And publishing attribution " +
                "WITHOUT initializing a context re-creates #737's defect — spans a governance platform " +
                "discards with no error, in a new place." +
                Environment.NewLine + Environment.NewLine +
                "If a new implementation of IAgentTelemetryAttribution genuinely needs to be listed, add " +
                "it to Exemptions above with the reason.");
        }
    }

    /// <summary>
    /// The guard is only meaningful if the member it names still exists — a rename would otherwise
    /// leave it scanning for a string nothing produces and passing forever.
    /// </summary>
    [Fact]
    public void TheGuardedMember_StillExists()
    {
        typeof(IAgentTelemetryAttribution)
            .GetMethod(Member)
            .Should().NotBeNull(
                $"this guard scans for the literal \"{Member}\"; if the method is renamed the scan " +
                "silently protects nothing, so rename it here too");
    }
}
