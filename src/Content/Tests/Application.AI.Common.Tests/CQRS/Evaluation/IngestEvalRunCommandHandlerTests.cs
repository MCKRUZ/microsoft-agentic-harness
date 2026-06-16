using Application.AI.Common.CQRS.Evaluation.IngestEvalRun;
using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Application.AI.Common.Evaluation.Notifications;
using Domain.AI.Evaluation;
using Domain.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.CQRS.Evaluation;

/// <summary>
/// Coverage for <see cref="IngestEvalRunCommandHandler"/>. Verifies the success path
/// (Inserted=true / Inserted=false), TimeProvider-sourced ReceivedAtUtc, and the
/// store-failure → Result&lt;T&gt;.Fail branch.
/// </summary>
public sealed class IngestEvalRunCommandHandlerTests
{
    private readonly Mock<IEvalRunStore> _store = new();
    private readonly Mock<IEvalRunNotifier> _notifier = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly IngestEvalRunCommandHandler _sut;

    public IngestEvalRunCommandHandlerTests()
    {
        _sut = new IngestEvalRunCommandHandler(
            _store.Object,
            _notifier.Object,
            _time,
            NullLogger<IngestEvalRunCommandHandler>.Instance);
    }

    private static EvalRunReport NewReport(string runId = "run-1") => new()
    {
        RunId = runId,
        StartedAtUtc = new DateTimeOffset(2026, 6, 1, 11, 0, 0, TimeSpan.Zero),
        CompletedAtUtc = new DateTimeOffset(2026, 6, 1, 11, 1, 0, TimeSpan.Zero),
        Duration = TimeSpan.FromMinutes(1),
        Datasets = [],
        Results = [],
        OverallVerdict = Verdict.Pass,
        Repeats = 1,
    };

    [Fact]
    public async Task Returns_Success_with_Inserted_true_on_first_ingest()
    {
        var report = NewReport();
        _store.Setup(s => s.AppendAsync(report, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(true);

        var result = await _sut.Handle(new IngestEvalRunCommand { Report = report }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.RunId.Should().Be("run-1");
        result.Value.Inserted.Should().BeTrue();
        result.Value.ReceivedAtUtc.Should().Be(_time.GetUtcNow());
    }

    [Fact]
    public async Task Returns_Success_with_Inserted_false_on_idempotent_reingest()
    {
        var report = NewReport();
        _store.Setup(s => s.AppendAsync(report, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(false);

        var result = await _sut.Handle(new IngestEvalRunCommand { Report = report }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Inserted.Should().BeFalse();
    }

    [Fact]
    public async Task Returns_Fail_when_store_throws()
    {
        var report = NewReport();
        _store.Setup(s => s.AppendAsync(It.IsAny<EvalRunReport>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new InvalidOperationException("disk full"));

        var result = await _sut.Handle(new IngestEvalRunCommand { Report = report }, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.General);
        // Raw exception text ("disk full") must NOT leak into the HTTP-facing Result;
        // the handler emits the stable scrubbed code instead (finding 56).
        result.Errors.Should().ContainSingle()
              .Which.Should().Be(IngestEvalRunCommandHandler.PersistFailedCode);
    }

    [Fact]
    public async Task Propagates_OperationCanceledException()
    {
        var report = NewReport();
        _store.Setup(s => s.AppendAsync(It.IsAny<EvalRunReport>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new OperationCanceledException());

        var act = () => _sut.Handle(new IngestEvalRunCommand { Report = report }, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ReceivedAtUtc_comes_from_TimeProvider_not_caller()
    {
        // Move the fake clock between construction and call to prove ReceivedAtUtc
        // is sampled at handler-invocation time, not at handler-construction time.
        var report = NewReport();
        var afterClock = _time.GetUtcNow().AddMinutes(5);
        _time.SetUtcNow(afterClock);

        DateTimeOffset capturedReceivedAt = default;
        _store.Setup(s => s.AppendAsync(report, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
              .Callback<EvalRunReport, DateTimeOffset, CancellationToken>((_, at, _) => capturedReceivedAt = at)
              .ReturnsAsync(true);

        var result = await _sut.Handle(new IngestEvalRunCommand { Report = report }, CancellationToken.None);

        capturedReceivedAt.Should().Be(afterClock);
        result.Value.ReceivedAtUtc.Should().Be(afterClock);
    }

    [Fact]
    public void Constructor_rejects_null_dependencies()
    {
        var act1 = () => new IngestEvalRunCommandHandler(null!, _notifier.Object, _time, NullLogger<IngestEvalRunCommandHandler>.Instance);
        var act2 = () => new IngestEvalRunCommandHandler(_store.Object, null!, _time, NullLogger<IngestEvalRunCommandHandler>.Instance);
        var act3 = () => new IngestEvalRunCommandHandler(_store.Object, _notifier.Object, null!, NullLogger<IngestEvalRunCommandHandler>.Instance);
        var act4 = () => new IngestEvalRunCommandHandler(_store.Object, _notifier.Object, _time, null!);

        act1.Should().Throw<ArgumentNullException>();
        act2.Should().Throw<ArgumentNullException>();
        act3.Should().Throw<ArgumentNullException>();
        act4.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Notifier_invoked_only_when_a_new_row_was_written()
    {
        var report = NewReport();
        _store.Setup(s => s.AppendAsync(report, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(true);

        await _sut.Handle(new IngestEvalRunCommand { Report = report }, CancellationToken.None);

        _notifier.Verify(
            n => n.NotifyRunCompletedAsync(It.IsAny<EvalRunSummary>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Notifier_skipped_on_idempotent_reingest()
    {
        var report = NewReport();
        _store.Setup(s => s.AppendAsync(report, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(false);

        await _sut.Handle(new IngestEvalRunCommand { Report = report }, CancellationToken.None);

        _notifier.Verify(
            n => n.NotifyRunCompletedAsync(It.IsAny<EvalRunSummary>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Notifier_failure_does_not_corrupt_success_outcome()
    {
        var report = NewReport();
        _store.Setup(s => s.AppendAsync(report, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(true);
        _notifier.Setup(n => n.NotifyRunCompletedAsync(It.IsAny<EvalRunSummary>(), It.IsAny<CancellationToken>()))
                 .ThrowsAsync(new InvalidOperationException("transport down"));

        var result = await _sut.Handle(new IngestEvalRunCommand { Report = report }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Inserted.Should().BeTrue();
    }

    [Fact]
    public async Task Notifier_cancellation_propagates()
    {
        var report = NewReport();
        _store.Setup(s => s.AppendAsync(report, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(true);
        _notifier.Setup(n => n.NotifyRunCompletedAsync(It.IsAny<EvalRunSummary>(), It.IsAny<CancellationToken>()))
                 .ThrowsAsync(new OperationCanceledException());

        var act = () => _sut.Handle(new IngestEvalRunCommand { Report = report }, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// Minimal <see cref="TimeProvider"/> shim returning a manually-controlled UTC time.
    /// Avoids pulling Microsoft.Extensions.TimeProvider.Testing into the test project
    /// just for this — the existing tests of MediatR behaviors use the same pattern.
    /// </summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset now) => _now = now;
        public void SetUtcNow(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
