using System.Text.RegularExpressions;
using FluentAssertions;
using Tests.Common;

namespace Application.AI.Common.Tests.Services.SkillTraining;

/// <summary>
/// Asserts that the proposer's side of skill training never reaches the scorer's types (#844): the
/// loop, its services and interfaces, its domain models, and the agentic proposer and its evidence tool.
/// </summary>
/// <remarks>
/// <para>
/// The proposer must be unable to learn what the grader looks for. The runtime fences (the pass/fail-only
/// <c>ProposerRolloutView</c>, the grader-marker scan on evidence) stop a verdict from being passed in;
/// this stops code from ever being written that reaches for the judge, a metric, its score, or a case's
/// answer key from that side. The scorer lives in <c>Infrastructure.AI.Evaluation.SkillTraining</c>
/// (the rollout runner), which is deliberately not scanned: producing the scores is its job.
/// </para>
/// <para>
/// Comments and strings are stripped before matching, so documentation may name these types; only code
/// that uses them fails.
/// </para>
/// </remarks>
public sealed class SkillTrainingDoesNotImportScorerTests
{
    private static readonly string[] ProposerSide =
    [
        Path.Combine("Application", "Application.AI.Common", "CQRS", "SkillTraining"),
        Path.Combine("Application", "Application.AI.Common", "Services", "SkillTraining"),
        Path.Combine("Application", "Application.AI.Common", "Interfaces", "SkillTraining"),
        Path.Combine("Domain", "Domain.AI", "SkillTraining"),
        Path.Combine("Infrastructure", "Infrastructure.AI", "SkillTraining"),
        Path.Combine("Infrastructure", "Infrastructure.AI", "Tools", "EvidenceReaderTool.cs"),
    ];

    private static readonly Regex Scorer = new(
        @"\b(ILlmJudge|JudgeCallCore|JuryLlmJudge|IEvalMetric|MetricScore|EvalCase|ExpectedOutput)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string ContentRoot = Path.Combine(RepoRoot.Path, "src", "Content");

    private static bool OnProposerSide(string path)
    {
        var relative = Path.GetRelativePath(ContentRoot, path);
        return ProposerSide.Any(p => relative.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheProposerSide_NeverReferencesTheJudgeAMetricAScoreOrAnAnswerKey()
    {
        var scanned = SourceScan.ReadProductionSources(ContentRoot).Where(f => OnProposerSide(f.Path)).ToList();

        scanned.Should().NotBeEmpty("a scan that found no files looks identical to a scan that found no offenders");
        var offenders = scanned
            .Where(f => Scorer.IsMatch(f.Code))
            .Select(f => $"{Path.GetRelativePath(ContentRoot, f.Path)}: {string.Join(", ", Scorer.Matches(f.Code).Select(m => m.Value).Distinct())}")
            .ToList();

        offenders.Should().BeEmpty(
            "the proposer must not be able to reach the scorer; it produces edits from evidence it is shown. Offenders:\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void TheScanSeesTheProposerAndTheLoop()
    {
        var names = SourceScan.ReadProductionSources(ContentRoot)
            .Where(f => OnProposerSide(f.Path))
            .Select(f => Path.GetFileName(f.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        names.Should().Contain(["AgenticPatchProposer.cs", "EvidenceReaderTool.cs", "TrainSkillCommandHandler.cs", "RolloutResult.cs"]);
    }

    [Fact]
    public void TheGuardWouldActuallyFire()
    {
        var violating = SourceScan.StripCommentsAndStrings("private readonly ILlmJudge _judge;");
        var answerKey = SourceScan.StripCommentsAndStrings("var key = @case.ExpectedOutput;");
        var commentOnly = SourceScan.StripCommentsAndStrings("// scored by an IEvalMetric elsewhere\npublic class X { }");

        Scorer.IsMatch(violating).Should().BeTrue();
        Scorer.IsMatch(answerKey).Should().BeTrue();
        Scorer.IsMatch(commentOnly).Should().BeFalse();
    }
}
