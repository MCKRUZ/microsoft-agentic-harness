using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.CQRS;

public class ExecuteAgentTurnCommandValidatorTests
{
    private readonly ExecuteAgentTurnCommandValidator _validator = new();

    private static ExecuteAgentTurnCommand CreateValidCommand() => new()
    {
        AgentName = "ResearchAgent",
        UserMessage = "Analyze the repository structure."
    };

    [Fact]
    public async Task Validate_NullAgentName_FailsValidation()
    {
        var command = CreateValidCommand() with { AgentName = null! };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == "AgentName");
    }

    [Fact]
    public async Task Validate_EmptyAgentName_FailsValidation()
    {
        var command = CreateValidCommand() with { AgentName = "" };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == "AgentName");
    }

    [Fact]
    public async Task Validate_EmptyUserMessage_FailsValidation()
    {
        var command = CreateValidCommand() with { UserMessage = "" };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == "UserMessage");
    }

    [Fact]
    public async Task Validate_UserMessageExceeding100KB_FailsValidation()
    {
        var command = CreateValidCommand() with { UserMessage = new string('x', 100_001) };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e =>
            e.PropertyName == "UserMessage" &&
            e.ErrorMessage.Contains("maximum length"));
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
