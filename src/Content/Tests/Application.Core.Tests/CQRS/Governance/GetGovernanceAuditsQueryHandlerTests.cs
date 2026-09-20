using Application.AI.Common.Interfaces.Governance;
using Application.Core.CQRS.Governance;
using Domain.AI.Governance;
using Domain.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.Governance;

/// <summary>
/// Tests for <see cref="GetGovernanceAuditsQueryHandler"/>: the read surfaces the audit
/// service's data as-is (no synthesis) and enforces its result cap by keeping the most
/// recent records.
/// </summary>
public sealed class GetGovernanceAuditsQueryHandlerTests
{
    [Fact]
    public async Task Handle_ReturnsServiceDataAndMapsFiltersThrough()
    {
        var now = DateTimeOffset.UtcNow;
        var records = new[] { CreateRecord(now) };
        var service = new Mock<IGovernanceAuditService>();
        GovernanceAuditQuery? captured = null;
        service
            .Setup(s => s.GetRecordsAsync(It.IsAny<GovernanceAuditQuery>(), It.IsAny<CancellationToken>()))
            .Callback<GovernanceAuditQuery, CancellationToken>((q, _) => captured = q)
            .ReturnsAsync(Result<IReadOnlyList<GovernanceAuditRecord>>.Success(records));
        var handler = new GetGovernanceAuditsQueryHandler(
            service.Object, NullLogger<GetGovernanceAuditsQueryHandler>.Instance);

        var result = await handler.Handle(new GetGovernanceAuditsQuery
        {
            Start = now.AddDays(-1),
            End = now,
            AgentId = "agent-1",
        }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(records);
        captured.Should().NotBeNull();
        captured!.AgentId.Should().Be("agent-1");
    }

    [Fact]
    public async Task Handle_StoreFailure_PropagatesFailureUnchanged()
    {
        var service = new Mock<IGovernanceAuditService>();
        service
            .Setup(s => s.GetRecordsAsync(It.IsAny<GovernanceAuditQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<GovernanceAuditRecord>>.Fail("disk unavailable"));
        var handler = new GetGovernanceAuditsQueryHandler(
            service.Object, NullLogger<GetGovernanceAuditsQueryHandler>.Instance);

        var result = await handler.Handle(new GetGovernanceAuditsQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse(
            "a read failure must never be silently reported as zero matching records");
    }

    [Fact]
    public async Task Handle_MoreMatchesThanCap_ReturnsMostRecentInChronologicalOrder()
    {
        var baseTime = new DateTimeOffset(2026, 7, 27, 0, 0, 0, TimeSpan.Zero);
        var records = Enumerable.Range(0, 5)
            .Select(i => CreateRecord(baseTime.AddMinutes(i)))
            .ToList();
        var service = new Mock<IGovernanceAuditService>();
        service
            .Setup(s => s.GetRecordsAsync(It.IsAny<GovernanceAuditQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<GovernanceAuditRecord>>.Success(records));
        var handler = new GetGovernanceAuditsQueryHandler(
            service.Object, NullLogger<GetGovernanceAuditsQueryHandler>.Instance);

        var result = await handler.Handle(
            new GetGovernanceAuditsQuery { MaxResults = 2 }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        result.Value.Should().ContainInOrder(records[3], records[4]);
    }

    [Fact]
    public async Task Handle_FewerMatchesThanCap_ReturnsAllUnmodified()
    {
        var records = new[] { CreateRecord(DateTimeOffset.UtcNow) };
        var service = new Mock<IGovernanceAuditService>();
        service
            .Setup(s => s.GetRecordsAsync(It.IsAny<GovernanceAuditQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<GovernanceAuditRecord>>.Success(records));
        var handler = new GetGovernanceAuditsQueryHandler(
            service.Object, NullLogger<GetGovernanceAuditsQueryHandler>.Instance);

        var result = await handler.Handle(
            new GetGovernanceAuditsQuery { MaxResults = 500 }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(records);
    }

    private static GovernanceAuditRecord CreateRecord(DateTimeOffset timestamp) => new()
    {
        Timestamp = timestamp,
        AgentId = "agent-1",
        Action = "run_tests",
        Decision = "allowed",
    };
}
