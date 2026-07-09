using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.Core.CQRS.Compliance.EraseMyData;
using FluentValidation.TestHelper;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.Compliance;

/// <summary>
/// Tests for <see cref="EraseMyDataCommandValidator"/> — the boundary rejects the destructive erasure
/// when no authenticated user scope is present.
/// </summary>
public sealed class EraseMyDataCommandValidatorTests
{
    private static EraseMyDataCommandValidator ValidatorWithUser(string? userId)
    {
        var scope = new Mock<IKnowledgeScope>();
        scope.SetupGet(s => s.UserId).Returns(userId);
        return new EraseMyDataCommandValidator(scope.Object);
    }

    [Fact]
    public void Validate_WithAuthenticatedScope_NoErrors()
    {
        var result = ValidatorWithUser("user-1").TestValidate(new EraseMyDataCommand());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_NoUserScope_HasError()
    {
        var result = ValidatorWithUser(null).TestValidate(new EraseMyDataCommand());
        result.ShouldHaveValidationErrorFor("Scope");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_BlankUserScope_HasError(string userId)
    {
        var result = ValidatorWithUser(userId).TestValidate(new EraseMyDataCommand());
        result.ShouldHaveValidationErrorFor("Scope");
    }
}
