using Application.AI.Common.Interfaces.Escalation;
using Application.Core.CQRS.Escalation;
using Domain.AI.Escalation;
using Domain.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.Escalation;

/// <summary>
/// Tests for <see cref="QueryEscalationAuditsQueryHandler"/> — mirrors
/// <c>DriftQueryHandlerTests</c>' coverage of <c>GetDriftAuditsQueryHandler</c>: the handler maps
/// every filter through to the store unchanged and caps an oversized match set to the most
/// recent records while keeping them in chronological order.
/// </summary>
public sealed class QueryEscalationAuditsQueryHandlerTests
{
    private static EscalationAuditRecord CreateRecord(DateTimeOffset timestamp, Guid? escalationId = null) => new()
    {
        RecordType = EscalationAuditRecordType.Request,
        EscalationId = escalationId ?? Guid.NewGuid(),
        Timestamp = timestamp,
        Payload = "{}"
    };

    [Fact]
    public async Task Handle_ReturnsStoreDataAndMapsFiltersThrough()
    {
        var now = DateTimeOffset.UtcNow;
        var escalationId = Guid.NewGuid();
        var records = new[] { CreateRecord(now, escalationId) };
        var store = new Mock<IEscalationAuditStore>();
        EscalationAuditQuery? captured = null;
        store
            .Setup(s => s.QueryAsync(It.IsAny<EscalationAuditQuery>(), It.IsAny<CancellationToken>()))
            .Callback<EscalationAuditQuery, CancellationToken>((q, _) => captured = q)
            .ReturnsAsync(Result<IReadOnlyList<EscalationAuditRecord>>.Success(records));
        var handler = new QueryEscalationAuditsQueryHandler(
            store.Object, NullLogger<QueryEscalationAuditsQueryHandler>.Instance);

        var result = await handler.Handle(new QueryEscalationAuditsQuery
        {
            Start = now.AddDays(-1),
            End = now,
            EscalationId = escalationId,
            RecordType = EscalationAuditRecordType.Request
        }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(records);
        captured.Should().NotBeNull();
        captured!.Start.Should().Be(now.AddDays(-1));
        captured.End.Should().Be(now);
        captured.EscalationId.Should().Be(escalationId);
        captured.RecordType.Should().Be(EscalationAuditRecordType.Request);
    }

    [Fact]
    public async Task Handle_StoreFailure_PropagatesTheFailureResult()
    {
        var store = new Mock<IEscalationAuditStore>();
        store
            .Setup(s => s.QueryAsync(It.IsAny<EscalationAuditQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<EscalationAuditRecord>>.Fail("disk read failed"));
        var handler = new QueryEscalationAuditsQueryHandler(
            store.Object, NullLogger<QueryEscalationAuditsQueryHandler>.Instance);

        var result = await handler.Handle(new QueryEscalationAuditsQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_MoreMatchesThanCap_ReturnsMostRecentInChronologicalOrder()
    {
        var baseTime = new DateTimeOffset(2026, 7, 27, 0, 0, 0, TimeSpan.Zero);
        var records = Enumerable.Range(0, 5)
            .Select(i => CreateRecord(baseTime.AddMinutes(i)))
            .ToList();
        var store = new Mock<IEscalationAuditStore>();
        store
            .Setup(s => s.QueryAsync(It.IsAny<EscalationAuditQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<EscalationAuditRecord>>.Success(records));
        var handler = new QueryEscalationAuditsQueryHandler(
            store.Object, NullLogger<QueryEscalationAuditsQueryHandler>.Instance);

        var result = await handler.Handle(
            new QueryEscalationAuditsQuery { MaxResults = 2 }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        result.Value.Should().ContainInOrder(records[3], records[4]);
    }

    [Fact]
    public async Task Handle_MatchesAtOrBelowCap_ReturnsAllUnchanged()
    {
        var records = new[] { CreateRecord(DateTimeOffset.UtcNow) };
        var store = new Mock<IEscalationAuditStore>();
        store
            .Setup(s => s.QueryAsync(It.IsAny<EscalationAuditQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<EscalationAuditRecord>>.Success(records));
        var handler = new QueryEscalationAuditsQueryHandler(
            store.Object, NullLogger<QueryEscalationAuditsQueryHandler>.Instance);

        var result = await handler.Handle(
            new QueryEscalationAuditsQuery { MaxResults = 10 }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(records);
    }
}
