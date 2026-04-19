using Domain.Common.Workflow;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="DecisionFramework"/> exercising validation,
/// outcome checking, and multi-rule evaluation scenarios end-to-end.
/// </summary>
public class DecisionFrameworkIntegrationTests
{
    private static DecisionFramework CreateValidationGateFramework() => new()
    {
        Type = "gate_decision",
        PossibleOutcomes = ["go", "conditional_go", "no_go"],
        DecisionRules =
        [
            new DecisionRule
            {
                Condition = "score >= 85 AND critical_issues == 0 AND high_issues <= 2",
                Outcome = "go",
                Description = "All quality criteria met"
            },
            new DecisionRule
            {
                Condition = "score >= 70 AND score < 85 AND critical_issues <= 1",
                Outcome = "conditional_go",
                Description = "Minor issues need addressing"
            },
            new DecisionRule
            {
                Condition = "score < 70 OR critical_issues > 1",
                Outcome = "no_go",
                Description = "Major quality issues"
            },
            new DecisionRule
            {
                Condition = "true",
                Outcome = "no_go",
                Description = "Default catch-all"
            }
        ],
        MetadataOutputs = ["decision", "score", "critical_issues", "high_issues", "conditions"]
    };

    [Fact]
    public void Validate_WellFormedFramework_ReturnsNoErrors()
    {
        var framework = CreateValidationGateFramework();

        var errors = framework.Validate();

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_EmptyOutcomes_ReturnsError()
    {
        var framework = new DecisionFramework
        {
            PossibleOutcomes = [],
            DecisionRules = [new DecisionRule { Condition = "true", Outcome = "go" }]
        };

        var errors = framework.Validate();

        errors.Should().Contain("possible_outcomes cannot be empty");
    }

    [Fact]
    public void Validate_EmptyRules_ReturnsError()
    {
        var framework = new DecisionFramework
        {
            PossibleOutcomes = ["go"],
            DecisionRules = []
        };

        var errors = framework.Validate();

        errors.Should().Contain("decision_rules cannot be empty");
    }

    [Fact]
    public void Validate_RuleOutcomeNotInPossibleOutcomes_ReturnsError()
    {
        var framework = new DecisionFramework
        {
            PossibleOutcomes = ["go", "no_go"],
            DecisionRules =
            [
                new DecisionRule { Condition = "score > 80", Outcome = "maybe" },
                new DecisionRule { Condition = "true", Outcome = "no_go" }
            ]
        };

        var errors = framework.Validate();

        errors.Should().Contain(e => e.Contains("'maybe'") && e.Contains("not in possible_outcomes"));
    }

    [Fact]
    public void Validate_EmptyCondition_ReturnsError()
    {
        var framework = new DecisionFramework
        {
            PossibleOutcomes = ["go"],
            DecisionRules =
            [
                new DecisionRule { Condition = "", Outcome = "go" },
                new DecisionRule { Condition = "true", Outcome = "go" }
            ]
        };

        var errors = framework.Validate();

        errors.Should().Contain("Decision rule cannot have empty condition");
    }

    [Fact]
    public void Validate_NoDefaultRule_ReturnsWarning()
    {
        var framework = new DecisionFramework
        {
            PossibleOutcomes = ["go", "no_go"],
            DecisionRules =
            [
                new DecisionRule { Condition = "score > 80", Outcome = "go" },
                new DecisionRule { Condition = "score <= 80", Outcome = "no_go" }
            ]
        };

        var errors = framework.Validate();

        errors.Should().Contain(e => e.Contains("No default rule"));
    }

    [Fact]
    public void Validate_DefaultRuleWithTrueCondition_NoWarning()
    {
        var framework = new DecisionFramework
        {
            PossibleOutcomes = ["go"],
            DecisionRules = [new DecisionRule { Condition = "true", Outcome = "go" }]
        };

        var errors = framework.Validate();

        errors.Should().NotContain(e => e.Contains("No default rule"));
    }

    [Fact]
    public void IsValidOutcome_KnownOutcome_ReturnsTrue()
    {
        var framework = CreateValidationGateFramework();

        framework.IsValidOutcome("go").Should().BeTrue();
        framework.IsValidOutcome("conditional_go").Should().BeTrue();
        framework.IsValidOutcome("no_go").Should().BeTrue();
    }

    [Fact]
    public void IsValidOutcome_UnknownOutcome_ReturnsFalse()
    {
        var framework = CreateValidationGateFramework();

        framework.IsValidOutcome("unknown").Should().BeFalse();
        framework.IsValidOutcome("").Should().BeFalse();
    }

    [Fact]
    public void DecisionResult_GoOutcome_CanProceed()
    {
        var result = new DecisionResult
        {
            Outcome = "go",
            IsSuccess = true,
            Metadata = new Dictionary<string, object>
            {
                ["score"] = 92,
                ["critical_issues"] = 0,
                ["high_issues"] = 1
            }
        };

        result.IsGo().Should().BeTrue();
        result.IsConditionalGo().Should().BeFalse();
        result.IsNoGo().Should().BeFalse();
        result.CanProceed().Should().BeTrue();
        result.GetScore().Should().Be(92);

        var (critical, high, medium, low) = result.GetIssueCounts();
        critical.Should().Be(0);
        high.Should().Be(1);
        medium.Should().Be(0);
        low.Should().Be(0);
    }

    [Fact]
    public void DecisionResult_ConditionalGoOutcome_CanProceedWithConditions()
    {
        var result = new DecisionResult
        {
            Outcome = "conditional_go",
            Conditions = ["Fix memory leak in service layer", "Add retry logic"],
            Metadata = new Dictionary<string, object>
            {
                ["score"] = 75,
                ["critical_issues"] = 0,
                ["high_issues"] = 3
            }
        };

        result.IsGo().Should().BeFalse();
        result.IsConditionalGo().Should().BeTrue();
        result.CanProceed().Should().BeTrue();
        result.Conditions.Should().HaveCount(2);
    }

    [Fact]
    public void DecisionResult_ConditionalAlias_IsRecognized()
    {
        var result = new DecisionResult { Outcome = "conditional" };

        result.IsConditionalGo().Should().BeTrue();
        result.CanProceed().Should().BeTrue();
    }

    [Fact]
    public void DecisionResult_NoGoOutcome_CannotProceed()
    {
        var result = new DecisionResult
        {
            Outcome = "no_go",
            IsSuccess = false,
            Metadata = new Dictionary<string, object>
            {
                ["score"] = 45,
                ["critical_issues"] = 3,
                ["high_issues"] = 7
            }
        };

        result.IsNoGo().Should().BeTrue();
        result.CanProceed().Should().BeFalse();
        result.GetScore().Should().Be(45);

        var (critical, high, _, _) = result.GetIssueCounts();
        critical.Should().Be(3);
        high.Should().Be(7);
    }

    [Fact]
    public void DecisionResult_CaseInsensitiveOutcomes_Work()
    {
        new DecisionResult { Outcome = "GO" }.IsGo().Should().BeTrue();
        new DecisionResult { Outcome = "Go" }.IsGo().Should().BeTrue();
        new DecisionResult { Outcome = "NO_GO" }.IsNoGo().Should().BeTrue();
        new DecisionResult { Outcome = "Conditional_Go" }.IsConditionalGo().Should().BeTrue();
    }

    [Fact]
    public void DecisionResult_MissingScoreMetadata_ReturnsZero()
    {
        var result = new DecisionResult { Outcome = "go", Metadata = [] };

        result.GetScore().Should().Be(0);
    }

    [Fact]
    public void DecisionResult_MissingIssueCountMetadata_ReturnsAllZeros()
    {
        var result = new DecisionResult { Outcome = "go", Metadata = [] };

        var (critical, high, medium, low) = result.GetIssueCounts();
        critical.Should().Be(0);
        high.Should().Be(0);
        medium.Should().Be(0);
        low.Should().Be(0);
    }

    [Fact]
    public void DecisionResult_MatchedRule_TracksWhichRuleMatched()
    {
        var rule = new DecisionRule
        {
            Condition = "score >= 85",
            Outcome = "go",
            Description = "High quality"
        };

        var result = new DecisionResult
        {
            Outcome = "go",
            MatchedRule = rule,
            Reason = "Score of 92 meets all thresholds"
        };

        result.MatchedRule.Should().NotBeNull();
        result.MatchedRule!.Description.Should().Be("High quality");
        result.Reason.Should().Contain("92");
    }

    [Fact]
    public void DecisionRule_Validate_EmptyConditionAndOutcome_ReturnsBothErrors()
    {
        var rule = new DecisionRule { Condition = "", Outcome = "" };

        var errors = rule.Validate();

        errors.Should().HaveCount(2);
        errors.Should().Contain("Rule condition cannot be empty");
        errors.Should().Contain("Rule outcome cannot be empty");
    }

    [Fact]
    public void DecisionRule_Validate_ValidRule_ReturnsNoErrors()
    {
        var rule = new DecisionRule
        {
            Condition = "score >= 85",
            Outcome = "go",
            Description = "Pass",
            Metadata = new Dictionary<string, object> { ["priority"] = 1 }
        };

        rule.Validate().Should().BeEmpty();
    }

    [Fact]
    public void FullDecisionWorkflow_FrameworkAndResult_WorkTogether()
    {
        var framework = CreateValidationGateFramework();

        // Simulate evaluation: score=78, critical=0, high=3
        var matchedRule = framework.DecisionRules
            .First(r => r.Outcome == "conditional_go");

        var result = new DecisionResult
        {
            Outcome = matchedRule.Outcome,
            MatchedRule = matchedRule,
            IsSuccess = true,
            Reason = "Score meets conditional threshold",
            Conditions = ["Address 3 high-severity issues"],
            Metadata = new Dictionary<string, object>
            {
                ["score"] = 78,
                ["critical_issues"] = 0,
                ["high_issues"] = 3
            }
        };

        // Verify against framework
        framework.IsValidOutcome(result.Outcome).Should().BeTrue();
        result.CanProceed().Should().BeTrue();
        result.IsConditionalGo().Should().BeTrue();
        result.GetScore().Should().Be(78);
    }
}
