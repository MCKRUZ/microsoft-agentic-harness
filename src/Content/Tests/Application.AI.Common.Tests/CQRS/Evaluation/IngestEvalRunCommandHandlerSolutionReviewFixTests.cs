using Application.AI.Common.CQRS.Evaluation.IngestEvalRun;
using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Notifications;
using Domain.AI.Evaluation;
using Domain.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.CQRS.Evaluation;

/// <summary>
/// Regression coverage for solution-review finding 56: raw store-exception messages
/// (which can embed absolute file paths, SQLite/EF provider internals, or connection
/// details) must not flow into <see cref="Result{T}.Fail(string[])"/> at this HTTP-facing
/// handler. The handler must instead emit the stable scrubbed code
/// <see cref="IngestEvalRunCommandHandler.PersistFailedCode"/> and log full detail only.
/// </summary>
public sealed class IngestEvalRunCommandHandlerSolutionReviewFixTests
{
    private readonly Mock<IEvalRunStore> _store = new();
    private readonly Mock<IEvalRunNotifier> _notifier = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly IngestEvalRunCommandHandler _sut;

    public IngestEvalRunCommandHandlerSolutionReviewFixTests()
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
    public async Task Handle_StoreThrows_ReturnsScrubbedCodeNotRawMessage()
    {
        // Arrange: a store exception whose message embeds a sensitive absolute path —
        // exactly the kind of detail that must never reach the HTTP caller.
        var report = NewReport();
        const string sensitiveDetail = @"C:\secrets\eval.db is locked by another process";
        _store.Setup(s => s.AppendAsync(
                   It.IsAny<EvalRunReport>(),
                   It.IsAny<DateTimeOffset>(),
                   It.IsAny<CancellationToken>()))
              .ThrowsAsync(new InvalidOperationException(sensitiveDetail));

        // Act
        var result = await _sut.Handle(new IngestEvalRunCommand { Report = report }, CancellationToken.None);

        // Assert: the result fails with the stable scrubbed code and leaks no raw detail.
        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainSingle()
              .Which.Should().Be(IngestEvalRunCommandHandler.PersistFailedCode);
        result.Errors.Should().NotContain(e => e.Contains("secrets"));
        result.Errors.Should().NotContain(e => e.Contains(sensitiveDetail));
    }

    /// <summary>
    /// Minimal <see cref="TimeProvider"/> shim returning a manually-controlled UTC time,
    /// mirroring the pattern used by the existing handler tests in this folder.
    /// </summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
