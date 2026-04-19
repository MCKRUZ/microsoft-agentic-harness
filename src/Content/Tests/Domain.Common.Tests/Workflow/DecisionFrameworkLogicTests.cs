using Domain.Common.Workflow;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Workflow;

/// <summary>
/// Tests for <see cref="DecisionFramework"/> logic methods: Validate, IsValidOutcome.
/// </summary>
public class DecisionFrameworkLogicTests
{
    private static DecisionFramework CreateValidFramework() => new()
    {
        Type = "gate_decision",
        PossibleOutcomes = ["go", "conditional_go", "no_go"],
        DecisionRules =
        [
            new DecisionRule { Condition = "score >= 85", Outcome = "go" },
            new DecisionRule { Condition = "score >= 70", Outcome = "conditional_go" },
            new DecisionRule { Condition = "true", Outcome = "no_go" }
        ],
        MetadataOutputs = ["score", "decision"]
    };

    // ── IsValidOutcome ──

    [Fact]
    public void IsValidOutcome_ValidOutcome_ReturnsTrue()
    {
        var framework = CreateValidFramework();

        framework.IsValidOutcome("go").Should().BeTrue();
    }

    [Fact]
    public void IsValidOutcome_InvalidOutcome_ReturnsFalse()
    {
        var framework = CreateValidFramework();

        framework.IsValidOutcome("unknown").Should().BeFalse();
    }

    // ── Validate ──

    [Fact]
    public void Validate_ValidFramework_ReturnsNoErrors()
    {
        var framework = CreateValidFramework();

        framework.Validate().Should().BeEmpty();
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

        errors.Should().Contain(e => e.Contains("possible_outcomes cannot be empty"));
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

        errors.Should().Contain(e => e.Contains("decision_rules cannot be empty"));
    }

    [Fact]
    public void Validate_RuleOutcomeNotInPossibleOutcomes_ReturnsError()
    {
        var framework = new DecisionFramework
        {
            PossibleOutcomes = ["go"],
            DecisionRules =
            [
                new DecisionRule { Condition = "true", Outcome = "invalid_outcome" }
            ]
        };

        var errors = framework.Validate();

        errors.Should().Contain(e => e.Contains("Rule outcome 'invalid_outcome'"));
    }

    [Fact]
    public void Validate_EmptyCondition_ReturnsError()
    {
        var framework = new DecisionFramework
        {
            PossibleOutcomes = ["go"],
            DecisionRules =
            [
                new DecisionRule { Condition = "", Outcome = "go" }
            ]
        };

        var errors = framework.Validate();

        errors.Should().Contain(e => e.Contains("empty condition"));
    }

    [Fact]
    public void Validate_NoDefaultRule_ReturnsWarning()
    {
        var framework = new DecisionFramework
        {
            PossibleOutcomes = ["go", "no_go"],
            DecisionRules =
            [
                new DecisionRule { Condition = "score >= 85", Outcome = "go" }
            ]
        };

        var errors = framework.Validate();

        errors.Should().Contain(e => e.Contains("No default rule found"));
    }

    [Fact]
    public void Validate_WithAlternativeDefaultRule_NoWarning()
    {
        var framework = new DecisionFramework
        {
            PossibleOutcomes = ["go"],
            DecisionRules =
            [
                new DecisionRule { Condition = "1 == 1", Outcome = "go" }
            ]
        };

        var errors = framework.Validate();

        errors.Should().NotContain(e => e.Contains("No default rule found"));
    }

    [Fact]
    public void Validate_MultipleErrors_ReturnsAll()
    {
        var framework = new DecisionFramework
        {
            PossibleOutcomes = [],
            DecisionRules = []
        };

        var errors = framework.Validate();

        errors.Should().HaveCountGreaterThanOrEqualTo(2);
    }
}
