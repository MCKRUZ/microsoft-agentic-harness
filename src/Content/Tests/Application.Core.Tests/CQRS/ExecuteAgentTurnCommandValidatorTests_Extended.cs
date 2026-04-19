using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.CQRS;

/// <summary>
/// Extended validator tests for <see cref="ExecuteAgentTurnCommandValidator"/>,
/// covering boundary values and additional edge cases not in the base test class.
/// </summary>
public class ExecuteAgentTurnCommandValidatorTests_Extended
{
    private readonly ExecuteAgentTurnCommandValidator _validator = new();

    private static ExecuteAgentTurnCommand CreateValidCommand() => new()
    {
        AgentName = "ResearchAgent",
        UserMessage = "Analyze this."
    };

    [Fact]
    public async Task Validate_WhitespaceOnlyAgentName_FailsValidation()
    {
        var command = CreateValidCommand() with { AgentName = "   " };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == "AgentName");
    }

    [Fact]
    public async Task Validate_WhitespaceOnlyUserMessage_FailsValidation()
    {
        var command = CreateValidCommand() with { UserMessage = "   " };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == "UserMessage");
    }

    [Fact]
    public async Task Validate_UserMessageExactly100KB_PassesValidation()
    {
        var command = CreateValidCommand() with { UserMessage = new string('x', 100_000) };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_SingleCharAgentName_PassesValidation()
    {
        var command = CreateValidCommand() with { AgentName = "A" };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_SingleCharUserMessage_PassesValidation()
    {
        var command = CreateValidCommand() with { UserMessage = "?" };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_BothFieldsEmpty_ReportsMultipleErrors()
    {
        var command = CreateValidCommand() with
        {
            AgentName = "",
            UserMessage = ""
        };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCountGreaterThanOrEqualTo(2);
        result.Errors.Should().Contain(e => e.PropertyName == "AgentName");
        result.Errors.Should().Contain(e => e.PropertyName == "UserMessage");
    }

    [Fact]
    public async Task Validate_NullUserMessage_FailsValidation()
    {
        var command = CreateValidCommand() with { UserMessage = null! };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == "UserMessage");
    }
}
