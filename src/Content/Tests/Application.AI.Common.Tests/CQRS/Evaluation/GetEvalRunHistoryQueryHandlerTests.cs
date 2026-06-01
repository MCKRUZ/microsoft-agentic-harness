using Application.AI.Common.CQRS.Evaluation.GetEvalRunHistory;
using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Domain.AI.Evaluation;
using Domain.Common;
using FluentAssertions;
using FluentValidation.TestHelper;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.CQRS.Evaluation;

public sealed class GetEvalRunHistoryQueryHandlerTests
{
    private readonly Mock<IEvalRunStore> _store = new();
    private readonly GetEvalRunHistoryQueryHandler _sut;

    public GetEvalRunHistoryQueryHandlerTests()
    {
        _sut = new GetEvalRunHistoryQueryHandler(
            _store.Object, NullLogger<GetEvalRunHistoryQueryHandler>.Instance);
    }

    private static EvalRunSummary Summary(string runId) => new()
    {
        RunId = runId,
        StartedAtUtc = DateTimeOffset.UtcNow,
        CompletedAtUtc = DateTimeOffset.UtcNow,
        Duration = TimeSpan.Zero,
        OverallVerdict = Verdict.Pass,
        ReceivedAtUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Returns_summaries_from_store()
    {
        var rows = (IReadOnlyList<EvalRunSummary>)[Summary("r1"), Summary("r2")];
        _store.Setup(s => s.GetRecentAsync(50, It.IsAny<CancellationToken>())).ReturnsAsync(rows);

        var result = await _sut.Handle(new GetEvalRunHistoryQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
    }

    [Fact]
    public async Task Propagates_OperationCanceledException()
    {
        _store.Setup(s => s.GetRecentAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new OperationCanceledException());

        var act = () => _sut.Handle(new GetEvalRunHistoryQuery(), CancellationToken.None);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Translates_store_failure_to_Fail()
    {
        _store.Setup(s => s.GetRecentAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new InvalidOperationException("db gone"));

        var result = await _sut.Handle(new GetEvalRunHistoryQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.General);
    }
}

public sealed class GetEvalRunHistoryQueryValidatorTests
{
    private readonly GetEvalRunHistoryQueryValidator _sut = new();

    [Fact]
    public void Default_Take_is_valid()
    {
        _sut.TestValidate(new GetEvalRunHistoryQuery()).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Zero_Take_fails()
    {
        _sut.TestValidate(new GetEvalRunHistoryQuery { Take = 0 })
            .ShouldHaveValidationErrorFor(q => q.Take);
    }

    [Fact]
    public void Over_max_Take_fails()
    {
        _sut.TestValidate(new GetEvalRunHistoryQuery
            { Take = GetEvalRunHistoryQueryValidator.MaxTake + 1 })
            .ShouldHaveValidationErrorFor(q => q.Take);
    }
}
