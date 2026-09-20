using Application.AI.Common.CQRS.Changes.GetChangeAudits;
using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using Domain.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.CQRS.Changes;

/// <summary>Handler tests for <see cref="GetChangeAuditsQueryHandler"/>.</summary>
public sealed class GetChangeAuditsQueryHandlerTests
{
    private static ChangeAuditRecord NewRecord(DateTimeOffset timestamp, string gateKey = "gate") => new()
    {
        Timestamp = timestamp,
        ProposalId = "p1",
        GateKey = gateKey,
        Decision = GateAction.Pass,
        BlastRadius = BlastRadius.Low,
        TargetKind = ChangeTargetKind.GitRepo,
        Mode = "Live",
        CorrelationId = "c1",
        AgentIdentity = new ChangeAuditIdentity { Agent = "agent-001", Kind = "ManagedIdentity" },
        DurationMs = 5,
    };

    [Fact]
    public async Task Handle_ReturnsStoreDataAndMapsFiltersThrough()
    {
        var store = new Mock<IChangeAuditWriter>();
        ChangeAuditQuery? captured = null;
        store.Setup(s => s.GetRecordsAsync(It.IsAny<ChangeAuditQuery>(), It.IsAny<CancellationToken>()))
            .Callback<ChangeAuditQuery, CancellationToken>((q, _) => captured = q)
            .ReturnsAsync(Result<IReadOnlyList<ChangeAuditRecord>>.Success([NewRecord(DateTimeOffset.UtcNow)]));
        var sut = new GetChangeAuditsQueryHandler(store.Object, NullLogger<GetChangeAuditsQueryHandler>.Instance);

        var result = await sut.Handle(
            new GetChangeAuditsQuery { ProposalId = "p1", GateKey = "gate-1", Decision = GateAction.Fail },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        captured!.ProposalId.Should().Be("p1");
        captured.GateKey.Should().Be("gate-1");
        captured.Decision.Should().Be(GateAction.Fail);
    }

    [Fact]
    public async Task Handle_MoreMatchesThanCap_ReturnsMostRecentInChronologicalOrder()
    {
        var baseTime = DateTimeOffset.UtcNow;
        var records = Enumerable.Range(0, 5)
            .Select(i => NewRecord(baseTime.AddMinutes(i), $"gate-{i}"))
            .ToList();
        var store = new Mock<IChangeAuditWriter>();
        store.Setup(s => s.GetRecordsAsync(It.IsAny<ChangeAuditQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<ChangeAuditRecord>>.Success(records));
        var sut = new GetChangeAuditsQueryHandler(store.Object, NullLogger<GetChangeAuditsQueryHandler>.Instance);

        var result = await sut.Handle(new GetChangeAuditsQuery { MaxResults = 2 }, CancellationToken.None);

        result.Value.Should().ContainInOrder(records[3], records[4]);
    }

    [Fact]
    public async Task Handle_StoreFails_PropagatesFailure()
    {
        var store = new Mock<IChangeAuditWriter>();
        store.Setup(s => s.GetRecordsAsync(It.IsAny<ChangeAuditQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<ChangeAuditRecord>>.Fail("boom"));
        var sut = new GetChangeAuditsQueryHandler(store.Object, NullLogger<GetChangeAuditsQueryHandler>.Instance);

        var result = await sut.Handle(new GetChangeAuditsQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
    }
}
