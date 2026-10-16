using Application.AI.Common.Services.SkillTraining;
using Domain.AI.SkillTraining;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Services.SkillTraining;

/// <summary>
/// Pins the grader-leak tripwire (#841). The markers are quoted JSON keys of our own result types, so
/// ordinary words like "score" and "hard" in tool output must not trip it; a serialized
/// <c>EvalRunReport</c> or <c>RolloutResult</c> must.
/// </summary>
public sealed class GraderMarkerScannerTests
{
    [Theory]
    [InlineData("The hard part was the score calculation; I scored it by hand.")]
    [InlineData("pass rate improved after the retry. Soft failures were ignored.")]
    [InlineData("")]
    public void Scan_OrdinaryTrajectoryText_FindsNothing(string text)
    {
        GraderMarkerScanner.Scan([("rollout:a", text)]).Should().BeEmpty();
    }

    [Theory]
    [InlineData("{\"OverallVerdict\":\"Pass\"}", "\"overallverdict\"")]
    [InlineData("{\"PassedCount\": 3}", "\"passedcount\"")]
    [InlineData("{\"Hard\": 0.0, \"Soft\": 0.2}", "\"hard\":")]
    [InlineData("{\"IsSuccess\":true}", "\"issuccess\"")]
    public void Scan_SerializedVerdictShapes_AreFound(string text, string expectedMarker)
    {
        var findings = GraderMarkerScanner.Scan([("rollout:a", text)]);

        findings.Should().Contain(f => f.Marker == expectedMarker && f.Source == "rollout:a");
    }

    [Fact]
    public void Scan_IsCaseInsensitive()
    {
        GraderMarkerScanner.Scan([("skill", "{\"overallVerdict\":\"Fail\"}")]).Should().NotBeEmpty();
    }

    [Fact]
    public void Scan_ReflectionInput_CoversTheSkillTheMemoryAndEveryTrajectory()
    {
        var input = new ReflectionInput
        {
            CurrentSkill = "{\"FailedCount\":1}",
            MetaSkillMemory = "{\"WarnedCount\":1}",
            Rollouts = [new ProposerRolloutView("case-7", false, "{\"ErroredCount\":1}", string.Empty)]
        };

        GraderMarkerScanner.Scan(input).Select(f => f.Source)
            .Should().BeEquivalentTo("skill", "meta-skill-memory", "rollout:case-7");
    }

    [Fact]
    public void Describe_NamesTheSourceAndMarkerButNeverTheScannedText()
    {
        const string secret = "sk-this-must-not-reach-a-log";
        var findings = GraderMarkerScanner.Scan([("rollout:a", $"{secret} {{\"OverallVerdict\":\"Fail\"}}")]);

        var described = GraderMarkerScanner.Describe(findings);

        described.Should().Contain("rollout:a").And.Contain("\"overallverdict\"");
        described.Should().NotContain(secret);
    }
}
