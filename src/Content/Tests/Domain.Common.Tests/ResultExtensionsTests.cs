using Domain.Common.Extensions;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests;

public class ResultExtensionsTests
{
    [Fact]
    public void Map_SuccessResult_TransformsValue()
    {
        var result = Result<int>.Success(5);

        var mapped = result.Map(v => v * 2);

        mapped.IsSuccess.Should().BeTrue();
        mapped.Value.Should().Be(10);
    }

    [Fact]
    public void Map_FailedResult_PropagatesFailure()
    {
        var result = Result<int>.Fail("bad");

        var mapped = result.Map(v => v * 2);

        mapped.IsSuccess.Should().BeFalse();
        mapped.Errors.Should().ContainSingle().Which.Should().Be("bad");
        mapped.FailureType.Should().Be(ResultFailureType.General);
    }

    [Fact]
    public void Bind_SuccessResult_ExecutesFunc()
    {
        var result = Result<int>.Success(5);

        var bound = result.Bind(v => Result<string>.Success(v.ToString()));

        bound.IsSuccess.Should().BeTrue();
        bound.Value.Should().Be("5");
    }

    [Fact]
    public void Bind_FailedResult_PropagatesFailure()
    {
        var result = Result<int>.Unauthorized("no access");

        var bound = result.Bind(v => Result<string>.Success(v.ToString()));

        bound.IsSuccess.Should().BeFalse();
        bound.FailureType.Should().Be(ResultFailureType.Unauthorized);
    }

    [Fact]
    public void Ensure_PredicateTrue_ReturnsOriginal()
    {
        var result = Result<int>.Success(10);

        var ensured = result.Ensure(v => v > 0, "must be positive");

        ensured.IsSuccess.Should().BeTrue();
        ensured.Value.Should().Be(10);
    }

    [Fact]
    public void Ensure_PredicateFalse_ReturnsFail()
    {
        var result = Result<int>.Success(-1);

        var ensured = result.Ensure(v => v > 0, "must be positive");

        ensured.IsSuccess.Should().BeFalse();
        ensured.Errors.Should().ContainSingle().Which.Should().Be("must be positive");
    }

    [Fact]
    public void OnSuccess_SuccessResult_ExecutesAction()
    {
        var result = Result<int>.Success(42);
        var captured = 0;

        result.OnSuccess(v => captured = v);

        captured.Should().Be(42);
    }

    [Fact]
    public void OnFailure_FailedResult_ExecutesAction()
    {
        var result = Result<int>.Fail("oops");
        IReadOnlyList<string>? captured = null;

        result.OnFailure(errors => captured = errors);

        captured.Should().NotBeNull();
        captured.Should().ContainSingle().Which.Should().Be("oops");
    }
}
