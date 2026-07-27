using Application.AI.Common.Interfaces.DriftDetection;
using Application.Core.CQRS.DriftDetection;
using Domain.AI.DriftDetection;
using Domain.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.DriftDetection;

/// <summary>
/// Tests for <see cref="RecalculateDriftBaselineCommandHandler"/>: id-to-scope resolution
/// through the store's indexed lookup with a clean 404 for unknown ids, delegation to the
/// subsystem's own recalculation path, a fail-closed audit gate, and — critically — an outcome
/// record that captures what the recalculation replaced and what it was built from, so the
/// "launder poisoned evaluations into a new normal" step is reconstructible.
/// </summary>
public sealed class RecalculateDriftBaselineCommandHandlerTests
{
    private readonly Mock<IDriftDetectionService> _driftService = new();
    private readonly Mock<IDriftBaselineStore> _baselineStore = new();
    private readonly Mock<IDriftAuditStore> _auditStore = new();
    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero));
    private readonly List<DriftAuditRecord> _audited = [];
    private readonly RecalculateDriftBaselineCommandHandler _handler;

    public RecalculateDriftBaselineCommandHandlerTests()
    {
        _auditStore
            .Setup(s => s.RecordAsync(It.IsAny<DriftAuditRecord>(), It.IsAny<CancellationToken>()))
            .Callback<DriftAuditRecord, CancellationToken>((r, _) => _audited.Add(r))
            .ReturnsAsync(Result.Success());
        _handler = new RecalculateDriftBaselineCommandHandler(
            _driftService.Object,
            _baselineStore.Object,
            _auditStore.Object,
            _timeProvider,
            NullLogger<RecalculateDriftBaselineCommandHandler>.Instance);
    }

    private static RecalculateDriftBaselineCommand CreateCommand(Guid baselineId) => new()
    {
        BaselineId = baselineId,
        CallerId = "ops@contoso.com"
    };

    private void SetupLookup(Guid baselineId, DriftBaseline? found) =>
        _baselineStore
            .Setup(s => s.GetBaselineByIdAsync(baselineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<DriftBaseline?>.Success(found));

    private DriftAuditRecord OutcomeRecord() =>
        _audited.Single(r => r.Payload.Contains("\"phase\":\"Outcome\""));

    [Fact]
    public async Task Handle_UnknownBaselineId_ReturnsNotFoundAndNeverRecalculates()
    {
        var id = Guid.NewGuid();
        SetupLookup(id, null);

        var result = await _handler.Handle(CreateCommand(id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.NotFound,
            "an unknown baseline id must map to 404 at the HTTP layer");
        _driftService.Verify(
            s => s.UpdateBaselineAsync(It.IsAny<DriftBaselineUpdateRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_UnknownBaselineId_StillAuditsTheAttempt()
    {
        var id = Guid.NewGuid();
        SetupLookup(id, null);

        await _handler.Handle(CreateCommand(id), CancellationToken.None);

        _audited.Should().HaveCount(2, "the attempt is recorded before the id is even resolved");
        _audited.Should().AllSatisfy(r =>
        {
            r.RecordType.Should().Be(DriftAuditRecordType.BaselineRecalculationRequested);
            r.Payload.Should().Contain("ops@contoso.com");
        });
        OutcomeRecord().Payload.Should().Contain("drift.not_found",
            "probing baseline ids is operator activity worth attributing");
    }

    [Fact]
    public async Task Handle_UsesIndexedLookupNotFullBaselineListing()
    {
        var id = Guid.NewGuid();
        SetupLookup(id, DriftTestData.CreateBaseline(baselineId: id));
        _driftService
            .Setup(s => s.UpdateBaselineAsync(It.IsAny<DriftBaselineUpdateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<DriftBaseline>.Success(DriftTestData.CreateBaseline()));

        await _handler.Handle(CreateCommand(id), CancellationToken.None);

        _baselineStore.Verify(s => s.GetBaselineByIdAsync(id, It.IsAny<CancellationToken>()), Times.Once);
        _baselineStore.Verify(
            s => s.GetBaselinesAsync(It.IsAny<DriftScope?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "resolving one id must not pull every baseline in the store on every request");
    }

    [Fact]
    public async Task Handle_KnownBaselineId_RecalculatesUsingTheTargetScopeAndAuditsCaller()
    {
        var id = Guid.NewGuid();
        var target = DriftTestData.CreateBaseline(
            baselineId: id, scope: DriftScope.TaskType, scopeIdentifier: "qa-check");
        var recalculated = DriftTestData.CreateBaseline(
            scope: DriftScope.TaskType, scopeIdentifier: "qa-check");
        SetupLookup(id, target);
        DriftBaselineUpdateRequest? capturedRequest = null;
        _driftService
            .Setup(s => s.UpdateBaselineAsync(It.IsAny<DriftBaselineUpdateRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DriftBaselineUpdateRequest, CancellationToken>((r, _) => capturedRequest = r)
            .ReturnsAsync(Result<DriftBaseline>.Success(recalculated));

        var result = await _handler.Handle(CreateCommand(id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(recalculated);
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Scope.Should().Be(DriftScope.TaskType,
            "recalculation must target the scope the id resolved to");
        capturedRequest.ScopeIdentifier.Should().Be("qa-check");

        var outcome = OutcomeRecord();
        outcome.EventId.Should().Be(recalculated.BaselineId,
            "the audit record must correlate to the new baseline snapshot");
        outcome.Payload.Should().Contain("ops@contoso.com");
        outcome.Payload.Should().Contain(DriftOperatorActionAudit.BaselineRecalculateAction);
    }

    [Fact]
    public async Task Handle_Success_AuditsPreviousBaselineIdAndTheWindowItConsumed()
    {
        var id = Guid.NewGuid();
        var target = DriftTestData.CreateBaseline(baselineId: id);
        var recalculated = DriftTestData.CreateBaseline() with
        {
            SampleCount = 42,
            WindowStart = new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero),
            WindowEnd = new DateTimeOffset(2026, 7, 27, 0, 0, 0, TimeSpan.Zero)
        };
        SetupLookup(id, target);
        _driftService
            .Setup(s => s.UpdateBaselineAsync(It.IsAny<DriftBaselineUpdateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<DriftBaseline>.Success(recalculated));

        await _handler.Handle(CreateCommand(id), CancellationToken.None);

        var payload = OutcomeRecord().Payload;
        payload.Should().Contain(id.ToString(),
            "the replaced baseline's id is the only surviving pointer to the prior 'normal' — SaveBaselineAsync overwrites it");
        payload.Should().Contain("\"sample_count\":42");
        payload.Should().Contain("2026-07-20",
            "the consumed window lets a reviewer re-query exactly which evaluations fed the new baseline");
        payload.Should().Contain("2026-07-27");
    }

    [Fact]
    public async Task Handle_AttemptAuditFails_RefusesRecalculationAndNeverResolvesTarget()
    {
        _auditStore
            .Setup(s => s.RecordAsync(It.IsAny<DriftAuditRecord>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Fail("audit store unavailable"));
        var id = Guid.NewGuid();
        SetupLookup(id, DriftTestData.CreateBaseline(baselineId: id));

        var result = await _handler.Handle(CreateCommand(id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Conflict);
        _driftService.Verify(
            s => s.UpdateBaselineAsync(It.IsAny<DriftBaselineUpdateRequest>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "recalculation destroys the previous snapshot; it must not run unattributed");
    }

    [Fact]
    public async Task Handle_InsufficientHistory_PropagatesConflictAndAuditsFailure()
    {
        var id = Guid.NewGuid();
        SetupLookup(id, DriftTestData.CreateBaseline(baselineId: id));
        _driftService
            .Setup(s => s.UpdateBaselineAsync(It.IsAny<DriftBaselineUpdateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<DriftBaseline>.Conflict("Insufficient samples: 3/20"));

        var result = await _handler.Handle(CreateCommand(id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Conflict,
            "too-few-samples is an expected state the HTTP layer maps to 409");
        OutcomeRecord().Payload.Should().Contain("drift.conflict");
    }

    [Fact]
    public async Task Handle_BaselineLookupFails_PropagatesFailure()
    {
        var id = Guid.NewGuid();
        _baselineStore
            .Setup(s => s.GetBaselineByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<DriftBaseline?>.Fail("store unavailable"));

        var result = await _handler.Handle(CreateCommand(id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        _driftService.Verify(
            s => s.UpdateBaselineAsync(It.IsAny<DriftBaselineUpdateRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
