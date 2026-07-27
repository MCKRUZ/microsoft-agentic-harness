using Application.AI.Common.Interfaces.DriftDetection;
using Application.Core.CQRS.DriftDetection;
using Domain.AI.DriftDetection;
using Domain.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.DriftDetection;

/// <summary>
/// Tests for the drift read-query handlers: each read surfaces the real store/service data
/// (no synthesis — an empty subsystem honestly returns empty), and the audits read enforces
/// its result cap by keeping the most recent records.
/// </summary>
public sealed class DriftQueryHandlerTests
{
    // == GetDriftBaselinesQuery ==

    [Fact]
    public async Task HandleBaselines_ReturnsStoreDataAndPassesScopeFilterThrough()
    {
        var baselines = new[] { DriftTestData.CreateBaseline(), DriftTestData.CreateBaseline() };
        var store = new Mock<IDriftBaselineStore>();
        store
            .Setup(s => s.GetBaselinesAsync(DriftScope.Skill, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<DriftBaseline>>.Success(baselines));
        var handler = new GetDriftBaselinesQueryHandler(
            store.Object, NullLogger<GetDriftBaselinesQueryHandler>.Instance);

        var result = await handler.Handle(
            new GetDriftBaselinesQuery { Scope = DriftScope.Skill }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(baselines,
            "the read must surface exactly what the store holds");
        store.Verify(s => s.GetBaselinesAsync(DriftScope.Skill, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleBaselines_EmptyStore_ReturnsEmptyListNotFailure()
    {
        var store = new Mock<IDriftBaselineStore>();
        store
            .Setup(s => s.GetBaselinesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<DriftBaseline>>.Success([]));
        var handler = new GetDriftBaselinesQueryHandler(
            store.Object, NullLogger<GetDriftBaselinesQueryHandler>.Instance);

        var result = await handler.Handle(new GetDriftBaselinesQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty(
            "a fresh deployment where nothing has pushed evaluations is an honest empty system");
    }

    // == GetDriftHistoryQuery ==

    [Fact]
    public async Task HandleHistory_DelegatesToServiceWithTheExactWindow()
    {
        var scores = new[] { DriftTestData.CreateScore() };
        var service = new Mock<IDriftDetectionService>();
        DriftHistoryQuery? captured = null;
        service
            .Setup(s => s.GetDriftHistoryAsync(It.IsAny<DriftHistoryQuery>(), It.IsAny<CancellationToken>()))
            .Callback<DriftHistoryQuery, CancellationToken>((q, _) => captured = q)
            .ReturnsAsync(Result<IReadOnlyList<DriftScore>>.Success(scores));
        var handler = new GetDriftHistoryQueryHandler(
            service.Object, NullLogger<GetDriftHistoryQueryHandler>.Instance);

        var start = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 7, 27, 0, 0, 0, TimeSpan.Zero);
        var result = await handler.Handle(new GetDriftHistoryQuery
        {
            Scope = DriftScope.Agent,
            ScopeIdentifier = "agent-1",
            Start = start,
            End = end
        }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(scores);
        captured.Should().NotBeNull();
        captured!.Scope.Should().Be(DriftScope.Agent);
        captured.ScopeIdentifier.Should().Be("agent-1");
        captured.Start.Should().Be(start);
        captured.End.Should().Be(end);
    }

    // == GetDriftAuditsQuery ==

    [Fact]
    public async Task HandleAudits_ReturnsStoreDataAndMapsFiltersThrough()
    {
        var now = DateTimeOffset.UtcNow;
        var records = new[] { DriftTestData.CreateAuditRecord(now) };
        var store = new Mock<IDriftAuditStore>();
        DriftAuditQuery? captured = null;
        store
            .Setup(s => s.GetRecordsAsync(It.IsAny<DriftAuditQuery>(), It.IsAny<CancellationToken>()))
            .Callback<DriftAuditQuery, CancellationToken>((q, _) => captured = q)
            .ReturnsAsync(Result<IReadOnlyList<DriftAuditRecord>>.Success(records));
        var handler = new GetDriftAuditsQueryHandler(
            store.Object, NullLogger<GetDriftAuditsQueryHandler>.Instance);

        var eventId = Guid.NewGuid();
        var result = await handler.Handle(new GetDriftAuditsQuery
        {
            Start = now.AddDays(-1),
            End = now,
            RecordType = DriftAuditRecordType.EvaluationPushed,
            EventId = eventId
        }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(records);
        captured.Should().NotBeNull();
        captured!.RecordType.Should().Be(DriftAuditRecordType.EvaluationPushed);
        captured.EventId.Should().Be(eventId);
    }

    [Fact]
    public async Task HandleAudits_MoreMatchesThanCap_ReturnsMostRecentInChronologicalOrder()
    {
        var baseTime = new DateTimeOffset(2026, 7, 27, 0, 0, 0, TimeSpan.Zero);
        var records = Enumerable.Range(0, 5)
            .Select(i => DriftTestData.CreateAuditRecord(baseTime.AddMinutes(i)))
            .ToList();
        var store = new Mock<IDriftAuditStore>();
        store
            .Setup(s => s.GetRecordsAsync(It.IsAny<DriftAuditQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<DriftAuditRecord>>.Success(records));
        var handler = new GetDriftAuditsQueryHandler(
            store.Object, NullLogger<GetDriftAuditsQueryHandler>.Instance);

        var result = await handler.Handle(
            new GetDriftAuditsQuery { MaxResults = 2 }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        // The cap keeps the newest activity, still chronologically ordered.
        result.Value.Should().ContainInOrder(records[3], records[4]);
    }
}
