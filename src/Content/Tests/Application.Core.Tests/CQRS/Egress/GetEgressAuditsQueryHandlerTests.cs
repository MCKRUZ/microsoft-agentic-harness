using Application.AI.Common.Interfaces.Egress;
using Application.Core.CQRS.Egress;
using Domain.AI.Egress;
using Domain.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.Egress;

/// <summary>
/// Tests for <see cref="GetEgressAuditsQueryHandler"/>: the handler surfaces the writer's data
/// verbatim (no synthesis) and enforces its result cap by keeping the most recent records.
/// </summary>
public sealed class GetEgressAuditsQueryHandlerTests
{
    private static EgressAuditRecord CreateRecord(DateTimeOffset timestamp, bool allowed = true) => new()
    {
        Timestamp = timestamp,
        Allowed = allowed,
        Target = "https://api.example.com/",
        Host = "api.example.com",
        Scheme = "https",
        Port = 443,
        Reason = "test",
        AgentIdentity = new EgressAuditIdentity { Agent = "agent-1", Kind = "Development" },
    };

    [Fact]
    public async Task Handle_ReturnsWriterDataAndMapsFiltersThrough()
    {
        var now = DateTimeOffset.UtcNow;
        var records = new[] { CreateRecord(now) };
        var writer = new Mock<IEgressAuditWriter>();
        EgressAuditQuery? captured = null;
        writer
            .Setup(w => w.GetRecordsAsync(It.IsAny<EgressAuditQuery>(), It.IsAny<CancellationToken>()))
            .Callback<EgressAuditQuery, CancellationToken>((q, _) => captured = q)
            .ReturnsAsync(Result<IReadOnlyList<EgressAuditRecord>>.Success(records));
        var handler = new GetEgressAuditsQueryHandler(writer.Object, NullLogger<GetEgressAuditsQueryHandler>.Instance);

        var result = await handler.Handle(new GetEgressAuditsQuery
        {
            Start = now.AddDays(-1),
            End = now,
            Allowed = true,
            Host = "api.example.com",
        }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(records);
        captured.Should().NotBeNull();
        captured!.Allowed.Should().Be(true);
        captured.Host.Should().Be("api.example.com");
    }

    [Fact]
    public async Task Handle_EmptyWriter_ReturnsEmptyListNotFailure()
    {
        var writer = new Mock<IEgressAuditWriter>();
        writer
            .Setup(w => w.GetRecordsAsync(It.IsAny<EgressAuditQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<EgressAuditRecord>>.Success([]));
        var handler = new GetEgressAuditsQueryHandler(writer.Object, NullLogger<GetEgressAuditsQueryHandler>.Instance);

        var result = await handler.Handle(new GetEgressAuditsQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_WriterFailure_PropagatesFailure()
    {
        var writer = new Mock<IEgressAuditWriter>();
        writer
            .Setup(w => w.GetRecordsAsync(It.IsAny<EgressAuditQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<EgressAuditRecord>>.Fail("disk error"));
        var handler = new GetEgressAuditsQueryHandler(writer.Object, NullLogger<GetEgressAuditsQueryHandler>.Instance);

        var result = await handler.Handle(new GetEgressAuditsQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_MoreMatchesThanCap_ReturnsMostRecentInChronologicalOrder()
    {
        var baseTime = new DateTimeOffset(2026, 7, 27, 0, 0, 0, TimeSpan.Zero);
        var records = Enumerable.Range(0, 5)
            .Select(i => CreateRecord(baseTime.AddMinutes(i)))
            .ToList();
        var writer = new Mock<IEgressAuditWriter>();
        writer
            .Setup(w => w.GetRecordsAsync(It.IsAny<EgressAuditQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<EgressAuditRecord>>.Success(records));
        var handler = new GetEgressAuditsQueryHandler(writer.Object, NullLogger<GetEgressAuditsQueryHandler>.Instance);

        var result = await handler.Handle(new GetEgressAuditsQuery { MaxResults = 2 }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        result.Value.Should().ContainInOrder(records[3], records[4]);
    }
}
