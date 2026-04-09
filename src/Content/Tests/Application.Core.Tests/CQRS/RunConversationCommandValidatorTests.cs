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
}
