using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests;

public class ResultTests
{
    [Fact]
    public void Success_CreatesSuccessResult()
    {
        var result = Result.Success();

        result.IsSuccess.Should().BeTrue();
        result.FailureType.Should().Be(ResultFailureType.None);
    }

    [Fact]
    public void Fail_CreatesFailureResult()
    {
        var result = Result.Fail("something went wrong");

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.General);
        result.Errors.Should().ContainSingle().Which.Should().Be("something went wrong");
    }

    [Fact]
    public void SuccessGeneric_CreatesResultWithValue()
    {
        var result = Result<int>.Success(42);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
        result.FailureType.Should().Be(ResultFailureType.None);
    }

    [Fact]
    public void FailGeneric_CreatesFailureWithoutValue()
    {
        var result = Result<int>.Fail("bad input");

        result.IsSuccess.Should().BeFalse();
        result.Value.Should().Be(default(int));
        result.Errors.Should().ContainSingle().Which.Should().Be("bad input");
    }

    [Fact]
    public void ValidationFailure_SetsCorrectType()
    {
        var errors = new List<string> { "Field is required", "Field too long" };

        var result = Result.ValidationFailure(errors);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Validation);
        result.Errors.Should().HaveCount(2);
    }

    [Fact]
    public void Unauthorized_SetsCorrectType()
    {
        var result = Result.Unauthorized("token expired");

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Unauthorized);
        result.Errors.Should().ContainSingle().Which.Should().Be("token expired");
    }

    [Fact]
    public void Forbidden_SetsForbiddenType()
    {
        var result = Result.Forbidden("admin only");

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Forbidden);
        result.Errors.Should().ContainSingle().Which.Should().Be("admin only");
    }

    [Fact]
    public void ContentBlocked_SetsCorrectType()
    {
        var result = Result.ContentBlocked("unsafe content detected");

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.ContentBlocked);
        result.Errors.Should().ContainSingle().Which.Should().Be("unsafe content detected");
    }

    [Fact]
    public void NotFound_SetsCorrectType()
    {
        var result = Result.NotFound("entity not found");

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.NotFound);
        result.Errors.Should().ContainSingle().Which.Should().Be("entity not found");
    }

    [Fact]
    public void PermissionRequired_SetsCorrectType()
    {
        var result = Result.PermissionRequired("file_write required");

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.PermissionRequired);
        result.Errors.Should().ContainSingle().Which.Should().Be("file_write required");
    }

    [Fact]
    public void Fail_WithMultipleErrors_StoresAll()
    {
        var result = Result.Fail("error one", "error two", "error three");

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().HaveCount(3);
        result.Errors.Should().ContainInOrder("error one", "error two", "error three");
    }

    [Fact]
    public void Success_ErrorsIsEmpty()
    {
        var result = Result.Success();

        result.Errors.Should().BeEmpty();
    }
}
