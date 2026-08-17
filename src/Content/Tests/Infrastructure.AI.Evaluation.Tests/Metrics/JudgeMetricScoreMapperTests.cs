using Application.AI.Common.Evaluation.Models;
using Application.AI.Common.Evaluation.Outcomes;
using Domain.AI.Evaluation;
using FluentAssertions;
using Infrastructure.AI.Evaluation.Metrics;
using Xunit;

namespace Infrastructure.AI.Evaluation.Tests.Metrics;

public sealed class JudgeMetricScoreMapperTests
{
    [Fact]
    public void Parsed_result_carries_violated_clause_and_evidence()
    {
        var result = new LlmJudgeResult
        {
            Outcome = LlmJudgeOutcome.Parsed,
            Score = 0.0,
            ViolatedClause = "must not leak secrets",
            Evidence = ["write_file"]
        };

        var score = JudgeMetricScoreMapper.ToMetricScore("llm_judge", result, threshold: 0.7, TimeSpan.Zero);

        score.ViolatedClause.Should().Be("must not leak secrets");
        score.Evidence.Should().ContainSingle().Which.Should().Be("write_file");
    }

    [Fact]
    public void ContractViolation_maps_to_Warn_and_does_not_surface_an_unverified_clause()
    {
        // Security-relevant: the JSON round-trip carries whatever the judge last wrote to
        // ViolatedClause even on a rejected response, so the mapper — not the judge's good
        // behaviour — is the only thing standing between an unverified claim and a report
        // reader mistaking it for a checked one.
        var result = new LlmJudgeResult
        {
            Outcome = LlmJudgeOutcome.ContractViolation,
            Score = 0.0,
            ViolatedClause = "a clause the judge invented and never actually passed verification",
            Evidence = ["some_tool"]
        };

        var score = JudgeMetricScoreMapper.ToMetricScore("llm_judge", result, threshold: 0.7, TimeSpan.Zero);

        score.Verdict.Should().Be(Verdict.Warn);
        score.ViolatedClause.Should().BeNull();
        score.Evidence.Should().ContainSingle();
    }

    [Fact]
    public void Legacy_parsed_result_produces_null_clause_and_empty_evidence()
    {
        var result = new LlmJudgeResult { Outcome = LlmJudgeOutcome.Parsed, Score = 0.9 };

        var score = JudgeMetricScoreMapper.ToMetricScore("faithfulness", result, threshold: 0.7, TimeSpan.Zero);

        score.ViolatedClause.Should().BeNull();
        score.Evidence.Should().NotBeNull().And.BeEmpty();
    }
}
