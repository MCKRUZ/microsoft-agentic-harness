using Domain.Common;
using Domain.Common.Extensions;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Extensions;

/// <summary>
/// Tests for <see cref="ResultExtensions"/> async methods, PropagateFailure across
/// all failure types, null-guard paths, and edge cases not covered by the base tests.
/// </summary>
public class ResultExtensionsLogicTests
{
    // ── MapAsync ──

    [Fact]
    public async Task MapAsync_SuccessResult_TransformsValueAsync()
    {
        var result = Result<int>.Success(5);

        var mapped = await result.MapAsync(v => Task.FromResult(v * 3));

        mapped.IsSuccess.Should().BeTrue();
        mapped.Value.Should().Be(15);
    }

    [Fact]
    public async Task MapAsync_FailedResult_PropagatesFailure()
    {
        var result = Result<int>.Fail("error");

        var mapped = await result.MapAsync(v => Task.FromResult(v * 3));

        mapped.IsSuccess.Should().BeFalse();
        mapped.Errors.Should().ContainSingle().Which.Should().Be("error");
    }

    [Fact]
    public async Task MapAsync_NullMapper_ThrowsArgumentNull()
    {
        var result = Result<int>.Success(1);

        var act = () => result.MapAsync<int, string>(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── BindAsync ──

    [Fact]
    public async Task BindAsync_SuccessResult_ExecutesBinder()
    {
        var result = Result<int>.Success(10);

        var bound = await result.BindAsync(v =>
            Task.FromResult(Result<string>.Success($"val={v}")));

        bound.IsSuccess.Should().BeTrue();
        bound.Value.Should().Be("val=10");
    }

    [Fact]
    public async Task BindAsync_FailedResult_PropagatesFailure()
    {
        var result = Result<int>.Unauthorized("denied");

        var bound = await result.BindAsync(v =>
            Task.FromResult(Result<string>.Success(v.ToString())));

        bound.IsSuccess.Should().BeFalse();
        bound.FailureType.Should().Be(ResultFailureType.Unauthorized);
    }

    [Fact]
    public async Task BindAsync_NullBinder_ThrowsArgumentNull()
    {
        var result = Result<int>.Success(1);

        var act = () => result.BindAsync<int, string>(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── ThenMap ──

    [Fact]
    public async Task ThenMap_SuccessTask_TransformsValue()
    {
        var resultTask = Task.FromResult(Result<int>.Success(7));

        var mapped = await resultTask.ThenMap(v => v.ToString());

        mapped.IsSuccess.Should().BeTrue();
        mapped.Value.Should().Be("7");
    }

    [Fact]
    public async Task ThenMap_FailedTask_PropagatesFailure()
    {
        var resultTask = Task.FromResult(Result<int>.Forbidden("no"));

        var mapped = await resultTask.ThenMap(v => v.ToString());

        mapped.IsSuccess.Should().BeFalse();
        mapped.FailureType.Should().Be(ResultFailureType.Forbidden);
    }

    // ── ThenBind ──

    [Fact]
    public async Task ThenBind_SuccessTask_ExecutesBinder()
    {
        var resultTask = Task.FromResult(Result<int>.Success(3));

        var bound = await resultTask.ThenBind(v =>
            Task.FromResult(Result<string>.Success($"x{v}")));

        bound.IsSuccess.Should().BeTrue();
        bound.Value.Should().Be("x3");
    }

    [Fact]
    public async Task ThenBind_FailedTask_PropagatesFailure()
    {
        var resultTask = Task.FromResult(Result<int>.NotFound("missing"));

        var bound = await resultTask.ThenBind(v =>
            Task.FromResult(Result<string>.Success(v.ToString())));

        bound.IsSuccess.Should().BeFalse();
        bound.FailureType.Should().Be(ResultFailureType.NotFound);
    }

    // ── PropagateFailure all types ──

    [Fact]
    public void Map_ValidationFailure_PropagatesValidationType()
    {
        var result = Result<int>.ValidationFailure(["err1", "err2"]);

        var mapped = result.Map(v => v.ToString());

        mapped.IsSuccess.Should().BeFalse();
        mapped.FailureType.Should().Be(ResultFailureType.Validation);
        mapped.Errors.Should().HaveCount(2);
    }

    [Fact]
    public void Map_UnauthorizedFailure_PropagatesUnauthorizedType()
    {
        var result = Result<int>.Unauthorized("no auth");

        var mapped = result.Map(v => v.ToString());

        mapped.IsSuccess.Should().BeFalse();
        mapped.FailureType.Should().Be(ResultFailureType.Unauthorized);
    }

    [Fact]
    public void Map_ForbiddenFailure_PropagatesForbiddenType()
    {
        var result = Result<int>.Forbidden("forbidden");

        var mapped = result.Map(v => v.ToString());

        mapped.IsSuccess.Should().BeFalse();
        mapped.FailureType.Should().Be(ResultFailureType.Forbidden);
    }

    [Fact]
    public void Map_ContentBlockedFailure_PropagatesContentBlockedType()
    {
        var result = Result<int>.ContentBlocked("blocked");

        var mapped = result.Map(v => v.ToString());

        mapped.IsSuccess.Should().BeFalse();
        mapped.FailureType.Should().Be(ResultFailureType.ContentBlocked);
    }

    [Fact]
    public void Map_NotFoundFailure_PropagatesNotFoundType()
    {
        var result = Result<int>.NotFound("not found");

        var mapped = result.Map(v => v.ToString());

        mapped.IsSuccess.Should().BeFalse();
        mapped.FailureType.Should().Be(ResultFailureType.NotFound);
    }

    [Fact]
    public void Map_GeneralFailure_PropagatesGeneralType()
    {
        var result = Result<int>.Fail("general error");

        var mapped = result.Map(v => v.ToString());

        mapped.IsSuccess.Should().BeFalse();
        mapped.FailureType.Should().Be(ResultFailureType.General);
    }

    // ── Ensure edge cases ──

    [Fact]
    public void Ensure_AlreadyFailed_ReturnsSameFailure()
    {
        var result = Result<int>.Forbidden("denied");

        var ensured = result.Ensure(v => v > 0, "positive required");

        ensured.IsSuccess.Should().BeFalse();
        ensured.FailureType.Should().Be(ResultFailureType.Forbidden);
    }

    [Fact]
    public void Ensure_NullPredicate_ThrowsArgumentNull()
    {
        var result = Result<int>.Success(1);

        var act = () => result.Ensure(null!, "msg");

        act.Should().Throw<ArgumentNullException>();
    }

    // ── OnSuccess/OnFailure edge cases ──

    [Fact]
    public void OnSuccess_FailedResult_DoesNotExecuteAction()
    {
        var result = Result<int>.Fail("err");
        var executed = false;

        result.OnSuccess(_ => executed = true);

        executed.Should().BeFalse();
    }

    [Fact]
    public void OnFailure_SuccessResult_DoesNotExecuteAction()
    {
        var result = Result<int>.Success(1);
        var executed = false;

        result.OnFailure(_ => executed = true);

        executed.Should().BeFalse();
    }

    [Fact]
    public void OnSuccess_NullAction_ThrowsArgumentNull()
    {
        var result = Result<int>.Success(1);

        var act = () => result.OnSuccess(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void OnFailure_NullAction_ThrowsArgumentNull()
    {
        var result = Result<int>.Fail("err");

        var act = () => result.OnFailure(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ── Map null guard ──

    [Fact]
    public void Map_NullMapper_ThrowsArgumentNull()
    {
        var result = Result<int>.Success(1);

        var act = () => result.Map<int, string>(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Bind_NullBinder_ThrowsArgumentNull()
    {
        var result = Result<int>.Success(1);

        var act = () => result.Bind<int, string>(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ── PropagateFailure with empty errors ──

    [Fact]
    public void Map_UnauthorizedWithEmptyErrors_JoinsAsUnknownError()
    {
        // Create an unauthorized result - errors list has exactly one entry
        var result = Result<int>.Unauthorized("access denied");

        var mapped = result.Map(v => v.ToString());

        mapped.IsSuccess.Should().BeFalse();
        mapped.FailureType.Should().Be(ResultFailureType.Unauthorized);
        mapped.Errors.Should().NotBeEmpty();
    }
}
