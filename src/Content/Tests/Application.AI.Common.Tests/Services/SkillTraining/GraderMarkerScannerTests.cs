using Application.AI.Common.Services.SkillTraining;
using Domain.AI.Evaluation;
using Domain.AI.SkillTraining;
using FluentAssertions;

namespace Application.AI.Common.Tests.Services.SkillTraining;

/// <summary>
/// Pins the grader-leak tripwire (#841). The markers are JSON keys of our own result types, so ordinary
/// words and ordinary JSON must not trip it (a refusal on a common string is a lever for tool output to
/// abort training runs); a serialized <c>EvalRunReport</c> or <c>RolloutResult</c> must, including the
/// JSON-escaped and spaced forms a report serialized into a trajectory takes.
/// </summary>
public sealed class GraderMarkerScannerTests
{
    [Theory]
    [InlineData("The hard part was the score calculation; I scored it by hand.")]
    [InlineData("pass rate improved after the retry. Soft failures were ignored.")]
    [InlineData("{\"isSuccess\": true, \"data\": {}}")]
    [InlineData("{\"Hard\": 4}")]
    [InlineData("{\"soft\": \"fork\"}")]
    [InlineData("")]
    public void Scan_OrdinaryTextAndOrdinaryJson_FindsNothing(string text)
    {
        GraderMarkerScanner.Scan([("rollout:a", text)]).Should().BeEmpty();
    }

    [Theory]
    [InlineData("{\"OverallVerdict\":\"Pass\"}", "OverallVerdict")]
    [InlineData("{\"PassedCount\": 3}", "PassedCount")]
    [InlineData("{\"FailedCount\" : 3}", "FailedCount")]
    [InlineData("{\\\"WarnedCount\\\":1}", "WarnedCount")]
    [InlineData("{\"overallverdict\":\"Fail\"}", "OverallVerdict")]
    [InlineData("{\"Hard\": 0.0, \"Soft\": 0.2}", "Hard+Soft")]
    [InlineData("{\\\"Soft\\\":0.2,\\\"Hard\\\":0.0}", "Hard+Soft")]
    public void Scan_SerializedVerdictShapes_AreFound(string text, string expectedMarker)
    {
        var findings = GraderMarkerScanner.Scan([("rollout:a", text)]);

        findings.Should().Contain(f => f.Marker == expectedMarker && f.Source == "rollout:a");
    }

    [Fact]
    public void Markers_NameRealProperties_SoARenameFailsHereAndNotSilently()
    {
        foreach (var key in GraderMarkerScanner.VerdictKeys)
            typeof(EvalRunReport).GetProperty(key).Should().NotBeNull($"'{key}' must be a property of EvalRunReport");

        foreach (var key in GraderMarkerScanner.ScoreKeys)
            typeof(RolloutResult).GetProperty(key).Should().NotBeNull($"'{key}' must be a property of RolloutResult");
    }

    [Fact]
    public void Screen_LeavesOutOnlyTheLeakingRollouts()
    {
        var rollouts = new[]
        {
            new ProposerRolloutView("clean-1", true, "all good", string.Empty),
            new ProposerRolloutView("leaky", false, "{\"OverallVerdict\":\"Fail\"}", string.Empty),
            new ProposerRolloutView("clean-2", false, "tool timed out", string.Empty),
        };

        var screened = GraderMarkerScanner.Screen(rollouts);

        screened.Clean.Select(r => r.ItemId).Should().Equal("clean-1", "clean-2");
        screened.Findings.Should().ContainSingle(f => f.Source == "rollout:leaky");
        screened.AnyExcluded.Should().BeTrue();
    }

    [Fact]
    public void Describe_NamesTheSourceAndMarkerButNeverTheScannedText()
    {
        const string secret = "sk-this-must-not-reach-a-log";
        var findings = GraderMarkerScanner.Scan([("rollout:a", $"{secret} {{\"OverallVerdict\":\"Fail\"}}")]);

        var described = GraderMarkerScanner.Describe(findings);

        described.Should().Contain("rollout:a").And.Contain("OverallVerdict");
        described.Should().NotContain(secret);
    }
}
