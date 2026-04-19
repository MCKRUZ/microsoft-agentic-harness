using Application.Core.CQRS.Agents.RunConversation;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.CQRS;

/// <summary>
/// Extended validator tests for <see cref="RunConversationCommandValidator"/>,
/// covering boundary values and additional edge cases.
/// </summary>
public class RunConversationCommandValidatorTests_Extended
{
    private readonly RunConversationCommandValidator _validator = new();

    private static RunConversationCommand CreateValidCommand() => new()
    {
        AgentName = "ResearchAgent",
        UserMessages = ["Hello"],
        MaxTurns = 10
    };

    [Fact]
    public async Task Validate_MaxTurnsExactlyOne_PassesValidation()
    {
        var command = CreateValidCommand() with { MaxTurns = 1 };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_MaxTurnsExactlyOneHundred_PassesValidation()
    {
        var command = CreateValidCommand() with { MaxTurns = 100 };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_NegativeMaxTurns_FailsValidation()
    {
        var command = CreateValidCommand() with { MaxTurns = -1 };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == "MaxTurns");
    }

    [Fact]
    public async Task Validate_WhitespaceOnlyAgentName_FailsValidation()
    {
        var command = CreateValidCommand() with { AgentName = "   " };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == "AgentName");
    }

    [Fact]
    public async Task Validate_NullAgentName_FailsValidation()
    {
        var command = CreateValidCommand() with { AgentName = null! };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == "AgentName");
    }

    [Fact]
    public async Task Validate_SingleUserMessage_PassesValidation()
    {
        var command = CreateValidCommand() with { UserMessages = ["One message"] };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_ManyUserMessages_PassesValidation()
    {
        var messages = Enumerable.Range(0, 50).Select(i => $"Message {i}").ToList();
        var command = CreateValidCommand() with { UserMessages = messages };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }
}
