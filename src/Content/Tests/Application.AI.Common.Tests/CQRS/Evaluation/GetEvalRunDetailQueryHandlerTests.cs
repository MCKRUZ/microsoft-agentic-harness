using Application.AI.Common.CQRS.Evaluation.GetEvalRunDetail;
using Application.AI.Common.Evaluation.Interfaces;
using Domain.AI.Evaluation;
using Domain.Common;
using FluentAssertions;
using FluentValidation.TestHelper;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.CQRS.Evaluation;

public sealed class GetEvalRunDetailQueryHandlerTests
{
    private readonly Mock<IEvalRunStore> _store = new();
    private readonly GetEvalRunDetailQueryHandler _sut;

    public GetEvalRunDetailQueryHandlerTests()
    {
        _sut = new GetEvalRunDetailQueryHandler(
            _store.Object, NullLogger<GetEvalRunDetailQueryHandler>.Instance);
    }

    private static EvalRunReport NewReport(string runId) => new()
    {
        RunId = runId,
        StartedAtUtc = DateTimeOffset.UtcNow,
        CompletedAtUtc = DateTimeOffset.UtcNow,
        Duration = TimeSpan.Zero,
        Datasets = [],
        Results = [],
        OverallVerdict = Verdict.Pass,
        Repeats = 1,
    };

    [Fact]
    public async Task Returns_Success_when_store_returns_report()
    {
        _store.Setup(s => s.GetRunDetailAsync("r1", It.IsAny<CancellationToken>()))
              .ReturnsAsync(NewReport("r1"));

        var result = await _sut.Handle(new GetEvalRunDetailQuery { RunId = "r1" }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.RunId.Should().Be("r1");
    }

    [Fact]
    public async Task Returns_NotFound_when_store_returns_null()
    {
        _store.Setup(s => s.GetRunDetailAsync("r-missing", It.IsAny<CancellationToken>()))
              .ReturnsAsync((EvalRunReport?)null);

        var result = await _sut.Handle(new GetEvalRunDetailQuery { RunId = "r-missing" }, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.NotFound);
    }

    [Fact]
    public async Task Translates_store_failure_to_Fail()
    {
        _store.Setup(s => s.GetRunDetailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new InvalidOperationException("io error"));

        var result = await _sut.Handle(new GetEvalRunDetailQuery { RunId = "r1" }, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.General);
    }
}

public sealed class GetEvalRunDetailQueryValidatorTests
{
    private readonly GetEvalRunDetailQueryValidator _sut = new();

    [Fact]
    public void Valid_query_has_no_errors()
        => _sut.TestValidate(new GetEvalRunDetailQuery { RunId = "r1" }).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Empty_RunId_fails()
        => _sut.TestValidate(new GetEvalRunDetailQuery { RunId = "" })
            .ShouldHaveValidationErrorFor(q => q.RunId);
}
