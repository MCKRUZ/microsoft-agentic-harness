using Domain.Common;
using Domain.Common.Helpers;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Helpers;

/// <summary>
/// Tests for <see cref="ResultHelper"/> TryCreateFailure and TryCreateValidationFailure
/// covering reflection-based factory method invocation, caching, and type mismatches.
/// </summary>
public class ResultHelperLogicTests
{
    // ── TryCreateFailure ──

    [Fact]
    public void TryCreateFailure_WithResultType_CreatesFailure()
    {
        var success = ResultHelper.TryCreateFailure<Result<string>>(
            nameof(Result.Unauthorized), "test error", out var result);

        success.Should().BeTrue();
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Be("test error");
    }

    [Fact]
    public void TryCreateFailure_Forbidden_CreatesForbiddenResult()
    {
        var success = ResultHelper.TryCreateFailure<Result<int>>(
            nameof(Result.Forbidden), "access denied", out var result);

        success.Should().BeTrue();
        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Forbidden);
    }

    [Fact]
    public void TryCreateFailure_Unauthorized_CreatesUnauthorizedResult()
    {
        var success = ResultHelper.TryCreateFailure<Result<int>>(
            nameof(Result.Unauthorized), "no auth", out var result);

        success.Should().BeTrue();
        result.FailureType.Should().Be(ResultFailureType.Unauthorized);
    }

    [Fact]
    public void TryCreateFailure_ContentBlocked_CreatesContentBlockedResult()
    {
        var success = ResultHelper.TryCreateFailure<Result<int>>(
            nameof(Result.ContentBlocked), "blocked", out var result);

        success.Should().BeTrue();
        result.FailureType.Should().Be(ResultFailureType.ContentBlocked);
    }

    [Fact]
    public void TryCreateFailure_NotFound_CreatesNotFoundResult()
    {
        var success = ResultHelper.TryCreateFailure<Result<int>>(
            nameof(Result.NotFound), "not found", out var result);

        success.Should().BeTrue();
        result.FailureType.Should().Be(ResultFailureType.NotFound);
    }

    [Fact]
    public void TryCreateFailure_PermissionRequired_CreatesPermissionRequiredResult()
    {
        var success = ResultHelper.TryCreateFailure<Result<int>>(
            nameof(Result.PermissionRequired), "need permission", out var result);

        success.Should().BeTrue();
        result.FailureType.Should().Be(ResultFailureType.PermissionRequired);
    }

    [Fact]
    public void TryCreateFailure_NonResultType_ReturnsFalse()
    {
        var success = ResultHelper.TryCreateFailure<string>(
            "Fail", "error", out var result);

        success.Should().BeFalse();
        result.Should().BeNull();
    }

    [Fact]
    public void TryCreateFailure_NonExistentMethod_ReturnsFalse()
    {
        var success = ResultHelper.TryCreateFailure<Result<int>>(
            "NonExistentMethod", "error", out _);

        success.Should().BeFalse();
    }

    // ── TryCreateValidationFailure ──

    [Fact]
    public void TryCreateValidationFailure_WithResultType_CreatesValidationFailure()
    {
        var errors = new List<string> { "err1", "err2" } as IReadOnlyList<string>;
        var success = ResultHelper.TryCreateValidationFailure<Result<int>>(
            errors, out var result);

        success.Should().BeTrue();
        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Validation);
        result.Errors.Should().HaveCount(2);
    }

    [Fact]
    public void TryCreateValidationFailure_NonResultType_ReturnsFalse()
    {
        var errors = new List<string> { "err1" } as IReadOnlyList<string>;
        var success = ResultHelper.TryCreateValidationFailure<string>(
            errors, out _);

        success.Should().BeFalse();
    }

    // ── Caching ──

    [Fact]
    public void TryCreateFailure_CalledTwice_UsesCachedMethod()
    {
        // First call populates cache
        ResultHelper.TryCreateFailure<Result<int>>(
            nameof(Result.Forbidden), "first", out var result1);
        // Second call hits cache
        ResultHelper.TryCreateFailure<Result<int>>(
            nameof(Result.Forbidden), "second", out var result2);

        result1!.Errors[0].Should().Be("first");
        result2!.Errors[0].Should().Be("second");
    }
}
