using Domain.Common.Workflow;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Workflow;

/// <summary>
/// Tests for <see cref="DecisionRule"/> — defaults, validation, and metadata.
/// </summary>
public class DecisionRuleTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var rule = new DecisionRule();

        rule.Condition.Should().BeEmpty();
        rule.Outcome.Should().BeEmpty();
        rule.Description.Should().BeNull();
        rule.Metadata.Should().BeNull();
    }

    [Fact]
    public void Validate_EmptyCondition_ReturnsError()
    {
        var rule = new DecisionRule { Outcome = "go" };

        var errors = rule.Validate();

        errors.Should().Contain("Rule condition cannot be empty");
    }

    [Fact]
    public void Validate_EmptyOutcome_ReturnsError()
    {
        var rule = new DecisionRule { Condition = "score >= 85" };

        var errors = rule.Validate();

        errors.Should().Contain("Rule outcome cannot be empty");
    }

    [Fact]
    public void Validate_BothEmpty_ReturnsBothErrors()
    {
        var rule = new DecisionRule();

        var errors = rule.Validate();

        errors.Should().HaveCount(2);
    }

    [Fact]
    public void Validate_ValidRule_ReturnsNoErrors()
    {
        var rule = new DecisionRule
        {
            Condition = "score >= 85",
            Outcome = "go"
        };

        var errors = rule.Validate();

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Metadata_CanBeSetAndRead()
    {
        var rule = new DecisionRule
        {
            Condition = "true",
            Outcome = "go",
            Metadata = new Dictionary<string, object> { ["priority"] = 1 }
        };

        rule.Metadata.Should().ContainKey("priority");
        rule.Metadata!["priority"].Should().Be(1);
    }
}
