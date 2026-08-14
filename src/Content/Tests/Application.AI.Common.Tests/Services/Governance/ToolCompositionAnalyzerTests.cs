using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Governance;
using Domain.Common.Config.AI.Governance;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Moq;
using Xunit;
using ToolCompositionAnalyzerImpl = Application.AI.Common.Services.Governance.ToolCompositionAnalyzer;

namespace Application.AI.Common.Tests.Services.Governance;

/// <summary>
/// Tests for <see cref="ToolCompositionAnalyzerImpl"/> — the build-time check for a source-capable
/// tool co-resident with a sink-capable tool in the same tool set.
/// </summary>
/// <remarks>
/// Findings are asserted independently of posture throughout: <see cref="ToolCompositionAnalyzerImpl"/>
/// reports every co-resident fact regardless of the configured posture — see
/// <see cref="ToolCompositionFinding"/>'s remarks for why filtering by posture happens later, live, at
/// the reporter and governor instead.
/// </remarks>
public sealed class ToolCompositionAnalyzerTests
{
    private const string SourceTool = "web_fetch";
    private const string SinkTool = "send_email";

    [Fact]
    public void Analyze_SourceAndSinkCoResident_ReturnsFindingNamingBothTools()
    {
        var analyzer = BuildAnalyzer(
            (SourceTool, ToolCompositionCapability.IngestsUntrustedInput),
            (SinkTool, ToolCompositionCapability.SendsOutbound));

        var assessment = analyzer.Analyze(Tools(SourceTool, SinkTool));

        assessment.Findings.Should().ContainSingle();
        var finding = assessment.Findings[0];
        finding.SourceTool.Should().Be(SourceTool);
        finding.SinkTool.Should().Be(SinkTool);
        finding.SourceCapability.Should().Be(ToolCompositionCapability.IngestsUntrustedInput);
        finding.SinkCapability.Should().Be(ToolCompositionCapability.SendsOutbound);
        finding.Path.Should().Contain(SourceTool).And.Contain(SinkTool);
    }

    [Fact]
    public void Analyze_SourceToolOnly_ReturnsNoFindings()
    {
        // Mandated control (acceptance criteria): an agent holding only the untrusted-input tool must
        // produce nothing. Mutation to run against this test: make an unclassified tool resolve to
        // ToolCompositionCapability's every bit rather than None — this test (and its sink-only sibling
        // below) must then fail, or the "universal taint destroys signal" failure is unguarded.
        var analyzer = BuildAnalyzer((SourceTool, ToolCompositionCapability.IngestsUntrustedInput));

        var assessment = analyzer.Analyze(Tools(SourceTool));

        assessment.Findings.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_SinkToolOnly_ReturnsNoFindings()
    {
        // The other half of the mandated control — see the source-only test's remarks.
        var analyzer = BuildAnalyzer((SinkTool, ToolCompositionCapability.SendsOutbound));

        var assessment = analyzer.Analyze(Tools(SinkTool));

        assessment.Findings.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_UnclassifiedTools_ReturnNoFindingsAndAreReportedAsUnclassified()
    {
        // The fail-open in practice: two unclassified tools together must not fire, and the analyzer
        // must say so was WHY nothing fired, rather than staying silent about the blind spot.
        var analyzer = BuildAnalyzer(
            (SourceTool, ToolCompositionCapability.None),
            (SinkTool, ToolCompositionCapability.None));

        var assessment = analyzer.Analyze(Tools(SourceTool, SinkTool));

        assessment.Findings.Should().BeEmpty();
        assessment.UnclassifiedTools.Should().BeEquivalentTo([SourceTool, SinkTool]);
    }

    [Fact]
    public void Analyze_ToolThatIsBothSourceAndSink_ProducesNoSelfFinding()
    {
        // Self-pairs are #324's job (behaviour gating for one tool), not a composition risk — see
        // ToolCompositionCapability's remarks.
        var analyzer = BuildAnalyzer(
            (SourceTool, ToolCompositionCapability.IngestsUntrustedInput | ToolCompositionCapability.SendsOutbound));

        var assessment = analyzer.Analyze(Tools(SourceTool));

        assessment.Findings.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_ToolThatIsBothSourceAndSink_StillCombinesWithADifferentTool()
    {
        // The self-pair exclusion must not also swallow a genuine cross-tool pairing involving the
        // same dual-capability tool.
        const string dualTool = "file_system";
        var analyzer = BuildAnalyzer(
            (dualTool, ToolCompositionCapability.IngestsUntrustedInput | ToolCompositionCapability.WritesFiles),
            (SinkTool, ToolCompositionCapability.SendsOutbound));

        var assessment = analyzer.Analyze(Tools(dualTool, SinkTool));

        assessment.Findings.Should().ContainSingle(f => f.SourceTool == dualTool && f.SinkTool == SinkTool);
    }

    [Fact]
    public void Analyze_ExactlyTheCapInRealPairings_IsNotReportedAsTruncated()
    {
        // The cap coinciding with the true last pairing must not be mislabelled as truncated — a false
        // "more were dropped" warning on a complete result is itself a wrong report, on a feature whose
        // whole design goal is accuracy about what is and is not present.
        var sourceNames = Enumerable.Range(0, 50).Select(i => $"source_{i}").ToArray();
        var profiles = sourceNames
            .Select(n => (n, ToolCompositionCapability.IngestsUntrustedInput))
            .Append((SinkTool, ToolCompositionCapability.SendsOutbound))
            .ToArray();
        var analyzer = BuildAnalyzer(profiles);

        var assessment = analyzer.Analyze(Tools([.. sourceNames, SinkTool]));

        assessment.Findings.Should().HaveCount(50);
        assessment.Truncated.Should().BeFalse();
    }

    [Fact]
    public void Analyze_MoreThanTheCap_IsReportedAsTruncated()
    {
        var sourceNames = Enumerable.Range(0, 51).Select(i => $"source_{i}").ToArray();
        var profiles = sourceNames
            .Select(n => (n, ToolCompositionCapability.IngestsUntrustedInput))
            .Append((SinkTool, ToolCompositionCapability.SendsOutbound))
            .ToArray();
        var analyzer = BuildAnalyzer(profiles);

        var assessment = analyzer.Analyze(Tools([.. sourceNames, SinkTool]));

        assessment.Findings.Should().HaveCount(50);
        assessment.Truncated.Should().BeTrue();
    }

    [Fact]
    public void Analyze_EmptyToolSet_ReturnsEmptyAssessment()
    {
        var analyzer = BuildAnalyzer();

        var assessment = analyzer.Analyze([]);

        assessment.Should().Be(Application.AI.Common.Interfaces.Governance.ToolCompositionAssessment.Empty);
    }

    /// <summary>Builds a real analyzer over a resolver stubbed to answer exactly the given profiles.</summary>
    private static ToolCompositionAnalyzerImpl BuildAnalyzer(params (string Name, ToolCompositionCapability Capabilities)[] profiles)
    {
        var resolver = new Mock<IToolCapabilityResolver>();
        foreach (var (name, capabilities) in profiles)
        {
            var origin = capabilities == ToolCompositionCapability.None
                ? ToolCapabilityOrigin.Unclassified
                : ToolCapabilityOrigin.FirstParty;
            resolver
                .Setup(r => r.Resolve(name))
                .Returns(new ToolCapabilityProfile(name, capabilities, origin));
        }

        return new ToolCompositionAnalyzerImpl(resolver.Object);
    }

    private static IReadOnlyList<AITool> Tools(params string[] names) =>
        names.Select(n => (AITool)AIFunctionFactory.Create(() => "ok", n)).ToList();
}
