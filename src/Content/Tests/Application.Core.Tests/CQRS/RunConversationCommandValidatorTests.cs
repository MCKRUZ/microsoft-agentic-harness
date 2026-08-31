using Application.Core.CQRS.Agents.RunConversation;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.CQRS;

public class RunConversationCommandValidatorTests
{
    private readonly RunConversationCommandValidator _validator = new();

    private static RunConversationCommand CreateValidCommand() => new()
    {
        AgentName = "ResearchAgent",
        UserMessages = ["What files exist?", "Summarize findings."],
        MaxTurns = 10
    };

    [Fact]
    public async Task Validate_EmptyAgentName_FailsValidation()
    {
        var command = CreateValidCommand() with { AgentName = "" };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == "AgentName");
    }

    [Fact]
    public async Task Validate_EmptyUserMessagesList_FailsValidation()
    {
        var command = CreateValidCommand() with { UserMessages = [] };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == "UserMessages");
    }

    [Fact]
    public async Task Validate_MaxTurnsBelowOne_FailsValidation()
    {
        var command = CreateValidCommand() with { MaxTurns = 0 };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == "MaxTurns");
    }

    [Fact]
    public async Task Validate_MaxTurnsAbove100_FailsValidation()
    {
        var command = CreateValidCommand() with { MaxTurns = 101 };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == "MaxTurns");
    }

    [Fact]
    public async Task Validate_ValidCommand_PassesValidation()
    {
        var command = CreateValidCommand();

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    // #560: ConversationId becomes a tool-result storage scope on every path, owner or not, so the
    // rule must not be gated on ConversationOwnerId being present — previously untested gap.
    [Fact]
    public async Task Validate_BlankConversationId_WithNoOwnerId_Fails()
    {
        // A blank value fails both the NotEmpty and the charset rule — two legitimate errors on
        // one property, not a bug — so this asserts at least one fires rather than exactly one.
        var command = CreateValidCommand() with { ConversationId = "", ConversationOwnerId = null };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "ConversationId");
    }

    [Fact]
    public async Task Validate_ConversationIdWithPathSeparator_Fails()
    {
        var command = CreateValidCommand() with { ConversationId = "../escape" };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == "ConversationId");
    }

    [Fact]
    public async Task Validate_ConversationIdShapedLikeAPlanStep_Passes()
    {
        // Regression: an earlier version of this validator's charset excluded ':' entirely, which
        // rejected PlanRunKeys.StepConversationId's own "{runScope}:{stepId}" shape — failing every
        // LLM step of every plan run. This id is exactly that shape.
        var command = CreateValidCommand() with { ConversationId = "a1b2c3d4-conv:step-7" };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }
}
