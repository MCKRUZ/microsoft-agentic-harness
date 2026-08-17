using Application.AI.Common.Evaluation.Models;
using Application.AI.Common.Evaluation.Outcomes;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Evaluation;

/// <summary>
/// Proves the strict-verdict-contract additions to <see cref="LlmJudgeRequest"/> and
/// <see cref="LlmJudgeResult"/> are genuinely additive: a caller that never sets them gets
/// exactly today's shape back, which is what lets the RAG judge metric pack and any
/// existing <c>ILlmJudge</c> caller stay byte-identical without code changes of their own.
/// </summary>
public sealed class LlmJudgeRequestResultTests
{
    [Fact]
    public void Request_without_verdict_contract_defaults_to_null()
    {
        var request = new LlmJudgeRequest
        {
            SystemPromptCore = "system",
            UserPromptTemplate = "user"
        };

        request.VerdictContract.Should().BeNull();
    }

    [Fact]
    public void Result_defaults_evidence_to_empty_and_violated_clause_to_null()
    {
        var result = new LlmJudgeResult
        {
            Outcome = LlmJudgeOutcome.Parsed,
            Score = 1.0
        };

        result.ViolatedClause.Should().BeNull();
        result.Evidence.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void JudgeVerdictContract_defaults_min_clause_length_to_12()
    {
        var contract = new JudgeVerdictContract
        {
            ClauseSource = "rubric text",
            FailingBelow = 0.7
        };

        contract.MinClauseLength.Should().Be(12);
    }
}
