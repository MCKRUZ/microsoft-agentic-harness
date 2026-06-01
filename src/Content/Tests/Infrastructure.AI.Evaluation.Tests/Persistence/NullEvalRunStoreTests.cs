using FluentAssertions;
using Infrastructure.AI.Evaluation.Persistence;
using Infrastructure.AI.Evaluation.Tests.Reporters;
using Xunit;

namespace Infrastructure.AI.Evaluation.Tests.Persistence;

public sealed class NullEvalRunStoreTests
{
    private readonly NullEvalRunStore _sut = new();
    private static readonly DateTimeOffset ReceivedAt =
        new(2026, 5, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AppendAsync_returns_false_and_persists_nothing()
    {
        var report = TestReportFactory.DeterministicReport();
        var written = await _sut.AppendAsync(report, ReceivedAt, CancellationToken.None);
        written.Should().BeFalse();
    }

    [Fact]
    public async Task GetRecentAsync_returns_empty_list()
    {
        var rows = await _sut.GetRecentAsync(10, CancellationToken.None);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRunDetailAsync_returns_null()
    {
        var actual = await _sut.GetRunDetailAsync("any", CancellationToken.None);
        actual.Should().BeNull();
    }

    [Fact]
    public async Task AppendAsync_validates_null_report()
    {
        var act = () => _sut.AppendAsync(null!, ReceivedAt, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task GetRecentAsync_validates_non_positive_take()
    {
        var act = () => _sut.GetRecentAsync(0, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task GetRunDetailAsync_validates_blank_run_id()
    {
        var act = () => _sut.GetRunDetailAsync("  ", CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }
}
