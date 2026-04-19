using Domain.Common.Helpers;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Helpers;

/// <summary>
/// Tests for <see cref="ResultHelper"/> reflection-based factory methods.
/// </summary>
public class ResultHelperTests
{
    [Fact]
    public void TryCreateFailure_WithResultType_CreatesFailure()
    {
        var success = ResultHelper.TryCreateFailure<Result<string>>(
            "Unauthorized", "token expired", out var result);

        success.Should().BeTrue();
        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Unauthorized);
        result.Errors.Should().ContainSingle().Which.Should().Be("token expired");
    }

    [Fact]
    public void TryCreateFailure_WithNonResultType_ReturnsFalse()
    {
        var success = ResultHelper.TryCreateFailure<string>(
            "Unauthorized", "token expired", out _);

        success.Should().BeFalse();
    }

    [Fact]
    public void TryCreateFailure_WithBaseResult_CreatesFailure()
    {
        var success = ResultHelper.TryCreateFailure<Result>(
            "NotFound", "entity missing", out var result);

        success.Should().BeTrue();
        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.NotFound);
    }

    [Fact]
    public void TryCreateFailure_WithInvalidMethodName_ReturnsFalse()
    {
        var success = ResultHelper.TryCreateFailure<Result<string>>(
            "NonExistentMethod", "reason", out _);

        success.Should().BeFalse();
    }

    [Fact]
    public void TryCreateValidationFailure_WithResultType_CreatesValidationFailure()
    {
        var errors = new List<string> { "Name required", "Email invalid" };

        var success = ResultHelper.TryCreateValidationFailure<Result<string>>(errors, out var result);

        success.Should().BeTrue();
        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Validation);
        result.Errors.Should().HaveCount(2);
    }

    [Fact]
    public void TryCreateValidationFailure_WithNonResultType_ReturnsFalse()
    {
        var errors = new List<string> { "error" };

        var success = ResultHelper.TryCreateValidationFailure<int>(errors, out _);

        success.Should().BeFalse();
    }

    [Fact]
    public void TryCreateFailure_Forbidden_CreatesCorrectResult()
    {
        var success = ResultHelper.TryCreateFailure<Result<int>>(
            "Forbidden", "admin only", out var result);

        success.Should().BeTrue();
        result.FailureType.Should().Be(ResultFailureType.Forbidden);
    }

    [Fact]
    public void TryCreateFailure_ContentBlocked_CreatesCorrectResult()
    {
        var success = ResultHelper.TryCreateFailure<Result<int>>(
            "ContentBlocked", "unsafe content", out var result);

        success.Should().BeTrue();
        result.FailureType.Should().Be(ResultFailureType.ContentBlocked);
    }

    [Fact]
    public void TryCreateFailure_PermissionRequired_CreatesCorrectResult()
    {
        var success = ResultHelper.TryCreateFailure<Result<int>>(
            "PermissionRequired", "file_write", out var result);

        success.Should().BeTrue();
        result.FailureType.Should().Be(ResultFailureType.PermissionRequired);
    }
}
