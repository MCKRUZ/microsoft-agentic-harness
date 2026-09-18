using Application.Core.CQRS.Agents.RefreshAgentRegistry;
using FluentAssertions;
using FluentValidation.TestHelper;
using Xunit;

namespace Application.Core.Tests.CQRS.Agents.RefreshAgentRegistry;

/// <summary>Tests for <see cref="RefreshAgentRegistryCommandValidator"/>.</summary>
public sealed class RefreshAgentRegistryCommandValidatorTests
{
    private readonly RefreshAgentRegistryCommandValidator _validator = new();

    [Fact]
    public void Validate_BlankCallerId_Fails()
    {
        var result = _validator.TestValidate(new RefreshAgentRegistryCommand { CallerId = "" });

        result.ShouldHaveValidationErrorFor(c => c.CallerId);
    }

    [Fact]
    public void Validate_CallerIdOverMaxLength_Fails()
    {
        var command = new RefreshAgentRegistryCommand
        {
            CallerId = new string('a', AgentRegistryValidationRules.MaxCallerIdLength + 1),
        };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(c => c.CallerId);
    }

    [Fact]
    public void Validate_ValidCallerId_Passes()
    {
        var result = _validator.TestValidate(new RefreshAgentRegistryCommand { CallerId = "ops@contoso.com" });

        result.ShouldNotHaveValidationErrorFor(c => c.CallerId);
    }

    [Fact]
    public void Validate_CallerIdAtMaxLength_Passes()
    {
        var command = new RefreshAgentRegistryCommand
        {
            CallerId = new string('a', AgentRegistryValidationRules.MaxCallerIdLength),
        };

        var result = _validator.TestValidate(command);

        result.ShouldNotHaveValidationErrorFor(c => c.CallerId);
    }
}
