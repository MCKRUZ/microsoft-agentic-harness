using Domain.Common;
using Domain.Common.Extensions;
using Domain.Common.Helpers;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="Result{T}"/> fluent extension chains,
/// implicit conversions, and <see cref="ResultHelper"/> reflection-based factory methods.
/// Exercises multi-step pipelines combining Map, Bind, Ensure, MapAsync, ThenMap, ThenBind.
/// </summary>
public class ResultChainIntegrationTests
{
    // ── Multi-step synchronous chains ──

    [Fact]
    public void MapBindEnsure_SuccessChain_ProducesExpectedResult()
    {
        var result = Result<int>.Success(10)
            .Map(x => x * 2)                                    // 20
            .Bind(x => x > 15
                ? Result<string>.Success($"value={x}")
                : Result<string>.Fail("too small"))             // "value=20"
            .Ensure(s => s.Contains("20"), "must contain 20");  // passes

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("value=20");
    }

    [Fact]
    public void MapBindEnsure_FailureInBind_PropagatesAndSkipsEnsure()
    {
        var result = Result<int>.Success(5)
            .Map(x => x * 2)                                    // 10
            .Bind(x => x > 15
                ? Result<string>.Success($"value={x}")
                : Result<string>.Fail("too small"))             // Fail
            .Ensure(s => s.Contains("20"), "must contain 20");  // skipped

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain("too small");
    }

    [Fact]
    public void MapBindEnsure_FailureInEnsure_ProducesFailure()
    {
        var result = Result<int>.Success(10)
            .Map(x => x * 2)                                    // 20
            .Bind(x => Result<string>.Success($"value={x}"))    // "value=20"
            .Ensure(s => s.Contains("99"), "must contain 99");  // fails

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain("must contain 99");
    }

    [Fact]
    public void OnSuccessOnFailure_SideEffects_ExecuteCorrectly()
    {
        var successActions = new List<string>();
        var failureActions = new List<string>();

        // Success path
        Result<int>.Success(42)
            .OnSuccess(v => successActions.Add($"got {v}"))
            .OnFailure(errors => failureActions.Add("failed"));

        successActions.Should().ContainSingle("got 42");
        failureActions.Should().BeEmpty();

        // Failure path
        successActions.Clear();
        Result<int>.Fail("oops")
            .OnSuccess(v => successActions.Add($"got {v}"))
            .OnFailure(errors => failureActions.Add($"failed: {errors[0]}"));

        successActions.Should().BeEmpty();
        failureActions.Should().ContainSingle("failed: oops");
    }

    // ── Multi-step async chains ──

    [Fact]
    public async Task MapAsyncBindAsync_SuccessChain_ProducesExpectedResult()
    {
        var result = await Result<int>.Success(5)
            .MapAsync(async x =>
            {
                await Task.Delay(1); // simulate async work
                return x * 10;
            })
            .ThenMap(x => $"result={x}");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("result=50");
    }

    [Fact]
    public async Task ThenBind_SuccessChain_ExecutesAllSteps()
    {
        var result = await Task.FromResult(Result<int>.Success(3))
            .ThenBind(async x =>
            {
                await Task.Delay(1);
                return Result<string>.Success($"step1={x}");
            });

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("step1=3");
    }

    [Fact]
    public async Task AsyncChain_FailureInMiddle_PropagatesCorrectType()
    {
        var result = await Result<int>.Success(10)
            .MapAsync(async x =>
            {
                await Task.Delay(1);
                return x.ToString();
            })
            .ThenBind(s => Task.FromResult(
                Result<double>.Unauthorized("no access")));

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Unauthorized);
    }

    // ── Failure type propagation across chains ──

    [Theory]
    [InlineData("Validation")]
    [InlineData("Unauthorized")]
    [InlineData("Forbidden")]
    [InlineData("ContentBlocked")]
    [InlineData("NotFound")]
    [InlineData("General")]
    public void Map_AllFailureTypes_PreserveTypeAcrossChain(string failureTypeName)
    {
        var source = CreateFailedResult<int>(failureTypeName);

        var result = source
            .Map(x => x.ToString())
            .Map(s => s.Length);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(Enum.Parse<ResultFailureType>(failureTypeName));
    }

    [Theory]
    [InlineData("Validation")]
    [InlineData("Unauthorized")]
    [InlineData("Forbidden")]
    [InlineData("ContentBlocked")]
    [InlineData("NotFound")]
    public async Task MapAsync_AllFailureTypes_PreserveTypeAcrossAsyncChain(string failureTypeName)
    {
        var source = CreateFailedResult<int>(failureTypeName);

        var result = await source
            .MapAsync(x => Task.FromResult(x.ToString()));

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(Enum.Parse<ResultFailureType>(failureTypeName));
    }

    // ── Implicit conversion ──

    [Fact]
    public void ImplicitConversion_NonNullValue_CreatesSuccessResult()
    {
        Result<string> result = "hello";

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("hello");
    }

    [Fact]
    public void ImplicitConversion_NullValue_ThrowsArgumentNullException()
    {
        var act = () => { Result<string> result = (string)null!; };

        act.Should().Throw<ArgumentNullException>();
    }

    // ── ResultHelper reflection-based factories ──

    [Fact]
    public void TryCreateFailure_ResultT_CreatesCorrectFailureType()
    {
        var success = ResultHelper.TryCreateFailure<Result<int>>(
            "Forbidden", "not allowed", out var result);

        success.Should().BeTrue();
        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Forbidden);
        result.Errors.Should().Contain("not allowed");
    }

    [Fact]
    public void TryCreateFailure_NonResultType_ReturnsFalse()
    {
        var success = ResultHelper.TryCreateFailure<string>(
            "Fail", "error", out _);

        success.Should().BeFalse();
    }

    [Fact]
    public void TryCreateValidationFailure_ResultT_CreatesValidationFailure()
    {
        var errors = new List<string> { "Field A required", "Field B invalid" };

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
        var success = ResultHelper.TryCreateValidationFailure<string>(
            ["error"], out _);

        success.Should().BeFalse();
    }

    // ── Result base class factory methods ──

    [Fact]
    public void ResultBase_AllFactoryMethods_ProduceCorrectFailureTypes()
    {
        Result.Success().IsSuccess.Should().BeTrue();
        Result.Fail("err").FailureType.Should().Be(ResultFailureType.General);
        Result.ValidationFailure(["e1"]).FailureType.Should().Be(ResultFailureType.Validation);
        Result.Unauthorized("u").FailureType.Should().Be(ResultFailureType.Unauthorized);
        Result.Forbidden("f").FailureType.Should().Be(ResultFailureType.Forbidden);
        Result.ContentBlocked("c").FailureType.Should().Be(ResultFailureType.ContentBlocked);
        Result.NotFound("n").FailureType.Should().Be(ResultFailureType.NotFound);
        Result.PermissionRequired("p").FailureType.Should().Be(ResultFailureType.PermissionRequired);
    }

    [Fact]
    public void ResultT_AllFactoryMethods_ProduceCorrectFailureTypes()
    {
        Result<int>.Success(1).IsSuccess.Should().BeTrue();
        Result<int>.Fail("err").FailureType.Should().Be(ResultFailureType.General);
        Result<int>.ValidationFailure(["e1"]).FailureType.Should().Be(ResultFailureType.Validation);
        Result<int>.Unauthorized("u").FailureType.Should().Be(ResultFailureType.Unauthorized);
        Result<int>.Forbidden("f").FailureType.Should().Be(ResultFailureType.Forbidden);
        Result<int>.ContentBlocked("c").FailureType.Should().Be(ResultFailureType.ContentBlocked);
        Result<int>.NotFound("n").FailureType.Should().Be(ResultFailureType.NotFound);
        Result<int>.PermissionRequired("p").FailureType.Should().Be(ResultFailureType.PermissionRequired);
    }

    [Fact]
    public void SuccessResult_FailureTypeIsNone_ErrorsAreEmpty()
    {
        var result = Result<int>.Success(42);

        result.FailureType.Should().Be(ResultFailureType.None);
        result.Errors.Should().BeEmpty();
        result.Value.Should().Be(42);
    }

    // ── Helper ──

    private static Result<T> CreateFailedResult<T>(string failureTypeName) => failureTypeName switch
    {
        "Validation" => Result<T>.ValidationFailure(["validation error"]),
        "Unauthorized" => Result<T>.Unauthorized("unauthorized"),
        "Forbidden" => Result<T>.Forbidden("forbidden"),
        "ContentBlocked" => Result<T>.ContentBlocked("blocked"),
        "NotFound" => Result<T>.NotFound("not found"),
        "General" => Result<T>.Fail("general error"),
        _ => throw new ArgumentException($"Unknown failure type: {failureTypeName}")
    };
}
