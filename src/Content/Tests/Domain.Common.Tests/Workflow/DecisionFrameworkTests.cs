using Domain.Common.Workflow;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Workflow;

/// <summary>
/// Tests for <see cref="DecisionFramework"/> — validation, outcome checking, and defaults.
/// </summary>
public class DecisionFrameworkTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var framework = new DecisionFramework();

        framework.Type.Should().Be("gate_decision");
        framework.PossibleOutcomes.Should().BeEmpty();
        framework.DecisionRules.Should().BeEmpty();
        framework.MetadataOutputs.Should().BeEmpty();
    }

    [Fact]
    public void Validate_EmptyOutcomes_ReturnsError()
    {
        var framework = new DecisionFramework();

        var errors = framework.Validate();

        errors.Should().Contain("possible_outcomes cannot be empty");
    }

    [Fact]
    public void Validate_EmptyRules_ReturnsError()
    {
        var framework = new DecisionFramework
        {
            PossibleOutcomes = ["go", "no_go"]
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
                new DecisionRule { Condition = "score >= 85", Outcome = "maybe" }
            ]
        };

        var errors = framework.Validate();

        errors.Should().Contain(e => e.Contains("'maybe' is not in possible_outcomes"));
    }

    [Fact]
    public void Validate_RuleWithEmptyCondition_ReturnsError()
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
                new DecisionRule { Condition = "score >= 85", Outcome = "go" }
            ]
        };

        var errors = framework.Validate();

        errors.Should().Contain(e => e.Contains("No default rule found"));
    }

    [Fact]
    public void Validate_WithDefaultRule_NoDefaultWarning()
    {
        var framework = new DecisionFramework
        {
            PossibleOutcomes = ["go", "no_go"],
            DecisionRules =
            [
                new DecisionRule { Condition = "score >= 85", Outcome = "go" },
                new DecisionRule { Condition = "true", Outcome = "no_go" }
            ]
        };

        var errors = framework.Validate();

        errors.Should().NotContain(e => e.Contains("No default rule found"));
    }

    [Fact]
    public void Validate_WithAlternateDefaultCondition_NoDefaultWarning()
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
    public void IsValidOutcome_ValidOutcome_ReturnsTrue()
    {
        var framework = new DecisionFramework
        {
            PossibleOutcomes = ["go", "no_go"]
        };

        framework.IsValidOutcome("go").Should().BeTrue();
    }

    [Fact]
    public void IsValidOutcome_InvalidOutcome_ReturnsFalse()
    {
        var framework = new DecisionFramework
        {
            PossibleOutcomes = ["go", "no_go"]
        };

        framework.IsValidOutcome("maybe").Should().BeFalse();
    }
}
