using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests;

/// <summary>
/// Tests for <see cref="Result{T}"/> — the generic result type including
/// all failure factory methods and the implicit conversion operator.
/// </summary>
public class ResultGenericTests
{
    [Fact]
    public void Success_CreatesResultWithValue()
    {
        var result = Result<string>.Success("hello");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("hello");
        result.FailureType.Should().Be(ResultFailureType.None);
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Fail_CreatesGenericFailure()
    {
        var result = Result<int>.Fail("bad input");

        result.IsSuccess.Should().BeFalse();
        result.Value.Should().Be(default(int));
        result.FailureType.Should().Be(ResultFailureType.General);
        result.Errors.Should().ContainSingle().Which.Should().Be("bad input");
    }

    [Fact]
    public void Fail_WithMultipleErrors_StoresAll()
    {
        var result = Result<int>.Fail("err1", "err2", "err3");

        result.Errors.Should().HaveCount(3);
        result.Errors.Should().ContainInOrder("err1", "err2", "err3");
    }

    [Fact]
    public void ValidationFailure_SetsCorrectType()
    {
        var errors = new List<string> { "Name required", "Age invalid" };

        var result = Result<string>.ValidationFailure(errors);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Validation);
        result.Errors.Should().HaveCount(2);
    }

    [Fact]
    public void Unauthorized_SetsCorrectType()
    {
        var result = Result<string>.Unauthorized("expired token");

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Unauthorized);
        result.Errors.Should().ContainSingle().Which.Should().Be("expired token");
    }

    [Fact]
    public void Forbidden_SetsCorrectType()
    {
        var result = Result<string>.Forbidden("admin only");

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Forbidden);
        result.Errors.Should().ContainSingle().Which.Should().Be("admin only");
    }

    [Fact]
    public void ContentBlocked_SetsCorrectType()
    {
        var result = Result<string>.ContentBlocked("unsafe");

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.ContentBlocked);
    }

    [Fact]
    public void NotFound_SetsCorrectType()
    {
        var result = Result<string>.NotFound("missing entity");

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.NotFound);
    }

    [Fact]
    public void PermissionRequired_SetsCorrectType()
    {
        var result = Result<string>.PermissionRequired("file_write");

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.PermissionRequired);
    }

    [Fact]
    public void ImplicitConversion_FromValue_CreatesSuccessResult()
    {
        Result<string> result = "hello";

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("hello");
    }

    [Fact]
    public void ImplicitConversion_FromNull_ThrowsArgumentNullException()
    {
        var act = () =>
        {
            Result<string> result = (string)null!;
        };

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Success_FailureTypeIsNone_Regardless()
    {
        var result = Result<int>.Success(42);

        result.FailureType.Should().Be(ResultFailureType.None);
    }
}
