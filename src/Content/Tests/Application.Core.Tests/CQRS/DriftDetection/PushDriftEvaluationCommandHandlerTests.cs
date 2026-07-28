using Application.AI.Common.Interfaces.DriftDetection;
using Application.Core.CQRS.DriftDetection;
using Domain.AI.DriftDetection;
using Domain.Common;
using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.DriftDetection;

/// <summary>
/// Tests for <see cref="PushDriftEvaluationCommandHandler"/>: the push runs through the real
/// drift pipeline (so it lands in EWMA state and history), every push is bracketed by
/// attempt/outcome audit records carrying the token-derived caller identity, an unattributable
/// push is refused outright, and a disabled subsystem refuses rather than reporting a hollow
/// success — because pushed evaluations shape what the subsystem considers "normal".
/// </summary>
public sealed class PushDriftEvaluationCommandHandlerTests
{
    private readonly Mock<IDriftDetectionService> _driftService = new();
    private readonly Mock<IDriftAuditStore> _auditStore = new();
    private readonly AppConfig _appConfig = new();
    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero));
    private readonly List<DriftAuditRecord> _audited = [];
    private readonly PushDriftEvaluationCommandHandler _handler;

    public PushDriftEvaluationCommandHandlerTests()
    {
        _auditStore
            .Setup(s => s.RecordAsync(It.IsAny<DriftAuditRecord>(), It.IsAny<CancellationToken>()))
            .Callback<DriftAuditRecord, CancellationToken>((r, _) => _audited.Add(r))
            .ReturnsAsync(Result.Success());
        _handler = new PushDriftEvaluationCommandHandler(
            _driftService.Object,
            _auditStore.Object,
            new StaticOptionsMonitor<AppConfig>(_appConfig),
            _timeProvider,
            NullLogger<PushDriftEvaluationCommandHandler>.Instance);
    }

    private static PushDriftEvaluationCommand CreateCommand(string callerId = "ops@contoso.com") => new()
    {
        Scope = DriftScope.Skill,
        ScopeIdentifier = "summarize",
        Dimensions = new Dictionary<DriftDimension, double>
        {
            [DriftDimension.Faithfulness] = 0.82,
            [DriftDimension.Relevance] = 0.9
        },
        CallerId = callerId
    };

    private void SetupPipelineSuccess(DriftScore score) =>
        _driftService
            .Setup(s => s.EvaluateDriftAsync(It.IsAny<DriftEvaluationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<DriftScore>.Success(score));

    [Fact]
    public async Task Handle_ServiceSucceeds_ReturnsScoreAndPassesDimensionsThroughUnchanged()
    {
        var score = DriftTestData.CreateScore();
        DriftEvaluationRequest? captured = null;
        _driftService
            .Setup(s => s.EvaluateDriftAsync(It.IsAny<DriftEvaluationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DriftEvaluationRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(Result<DriftScore>.Success(score));

        var command = CreateCommand();
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(score, "the handler must surface the pipeline's real score");
        captured.Should().NotBeNull();
        captured!.Scope.Should().Be(command.Scope);
        captured.ScopeIdentifier.Should().Be(command.ScopeIdentifier);
        captured.Dimensions.Should().BeEquivalentTo(command.Dimensions,
            "pushed scores must reach the EWMA pipeline exactly as validated");
    }

    [Fact]
    public async Task Handle_ServiceSucceeds_AuditsAttemptThenOutcomeWithSharedActionId()
    {
        var score = DriftTestData.CreateScore();
        SetupPipelineSuccess(score);

        await _handler.Handle(CreateCommand("ops@contoso.com"), CancellationToken.None);

        _audited.Should().HaveCount(2, "every push is bracketed by an attempt and an outcome record");
        _audited.Should().AllSatisfy(r =>
        {
            r.RecordType.Should().Be(DriftAuditRecordType.EvaluationPushed);
            r.Payload.Should().Contain("ops@contoso.com", "the caller identity is the point of the trail");
            r.Payload.Should().Contain(DriftOperatorActionAudit.EvaluationPushAction);
        });

        _audited[0].Payload.Should().Contain("\"phase\":\"Attempt\"");
        _audited[1].Payload.Should().Contain("\"phase\":\"Outcome\"");
        _audited[1].EventId.Should().Be(score.ScoreId,
            "the outcome record must correlate to the produced score for eventId queries");

        var attemptActionId = _audited[0].EventId;
        _audited[1].Payload.Should().Contain(attemptActionId.ToString(),
            "both halves share an ActionId so an attempt with no outcome is detectable");
    }

    [Fact]
    public async Task Handle_AttemptAuditFails_RefusesPushAndNeverReachesPipeline()
    {
        _auditStore
            .Setup(s => s.RecordAsync(It.IsAny<DriftAuditRecord>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Fail("audit store unavailable"));
        SetupPipelineSuccess(DriftTestData.CreateScore());

        var result = await _handler.Handle(CreateCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse(
            "an unattributable push must be refused, not allowed through silently");
        result.FailureType.Should().Be(ResultFailureType.Conflict);
        _driftService.Verify(
            s => s.EvaluateDriftAsync(It.IsAny<DriftEvaluationRequest>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "filling the disk must not become a way to poison EWMA state with no trail");
    }

    [Fact]
    public async Task Handle_AttemptAuditThrows_RefusesPushAndNeverReachesPipeline()
    {
        _auditStore
            .Setup(s => s.RecordAsync(It.IsAny<DriftAuditRecord>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnauthorizedAccessException("AuditPath is not writable"));
        SetupPipelineSuccess(DriftTestData.CreateScore());

        var result = await _handler.Handle(CreateCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Conflict);
        result.Errors.Should().NotContain(e => e.Contains("AuditPath"),
            "store internals must never reach the caller");
        _driftService.Verify(
            s => s.EvaluateDriftAsync(It.IsAny<DriftEvaluationRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_DriftDetectionDisabled_ReturnsConflictAndNeverReachesPipeline()
    {
        _appConfig.AI.DriftDetection.Enabled = false;
        SetupPipelineSuccess(DriftTestData.CreateScore());

        var result = await _handler.Handle(CreateCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse(
            "the service's no-op success would report a push that never happened — through HTTP that is a 200 lie");
        result.FailureType.Should().Be(ResultFailureType.Conflict,
            "matching UpdateBaselineAsync's posture for the identical condition, and the controller's documented 409");
        _driftService.Verify(
            s => s.EvaluateDriftAsync(It.IsAny<DriftEvaluationRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_DriftDetectionDisabled_AuditsRejectionAsFailedNotSucceeded()
    {
        _appConfig.AI.DriftDetection.Enabled = false;
        SetupPipelineSuccess(DriftTestData.CreateScore());

        await _handler.Handle(CreateCommand(), CancellationToken.None);

        var outcome = _audited.Single(r => r.Payload.Contains("\"phase\":\"Outcome\""));
        outcome.Payload.Should().Contain("\"succeeded\":false",
            "a disabled subsystem must not leave a trail claiming successful monitoring");
        outcome.Payload.Should().Contain("drift.conflict");
        outcome.Payload.Should().NotContain("correlation_id",
            "there is no score to correlate to when the push never ran");
    }

    [Fact]
    public async Task Handle_ServiceFails_PropagatesFailureTypeAndAuditsFailedAttempt()
    {
        _driftService
            .Setup(s => s.EvaluateDriftAsync(It.IsAny<DriftEvaluationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<DriftScore>.Conflict("No baseline available for scope Skill:summarize"));

        var result = await _handler.Handle(CreateCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Conflict,
            "no-baseline is an expected state the HTTP layer maps to 409");

        var outcome = _audited.Single(r => r.Payload.Contains("\"phase\":\"Outcome\""));
        outcome.Payload.Should().Contain("drift.conflict");
        outcome.Payload.Should().NotContain("No baseline available",
            "audit failure codes are stable classifications, never raw error text");
    }

    [Fact]
    public async Task Handle_OutcomeAuditThrowsAfterSuccessfulAttempt_StillReturnsServiceResult()
    {
        var score = DriftTestData.CreateScore();
        SetupPipelineSuccess(score);
        var call = 0;
        _auditStore
            .Setup(s => s.RecordAsync(It.IsAny<DriftAuditRecord>(), It.IsAny<CancellationToken>()))
            .Returns<DriftAuditRecord, CancellationToken>((_, _) =>
                ++call == 1
                    ? Task.FromResult(Result.Success())
                    : throw new IOException("disk full"));

        var result = await _handler.Handle(CreateCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(
            "the attempt record already made the push attributable, so losing the outcome record must not fail the operation");
    }
}

/// <summary>Minimal <see cref="IOptionsMonitor{T}"/> over a single mutable instance.</summary>
internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
