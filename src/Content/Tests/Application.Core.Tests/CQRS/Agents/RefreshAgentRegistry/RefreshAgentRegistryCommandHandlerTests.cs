using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.Core.CQRS.Agents.RefreshAgentRegistry;
using Domain.AI.Agents;
using Domain.Common.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.Agents.RefreshAgentRegistry;

/// <summary>
/// Tests for <see cref="RefreshAgentRegistryCommandHandler"/>: it drives
/// <see cref="IAgentRegistryRefresher.Refresh"/>, records the request and its outcome through the
/// general-purpose <see cref="IAuditSink"/>, and — unlike the drift write commands — stays
/// available (fail-open) when the audit sink itself is unavailable, since a registry refresh is a
/// trivially repeatable read of files already on disk, not a mutation with a poisoning threat model.
/// </summary>
public sealed class RefreshAgentRegistryCommandHandlerTests
{
    private readonly Mock<IAgentRegistryRefresher> _refresher = new();
    private readonly Mock<IAuditSink> _auditSink = new();
    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero));
    private readonly List<AuditEntry> _audited = [];
    private readonly RefreshAgentRegistryCommandHandler _handler;

    public RefreshAgentRegistryCommandHandlerTests()
    {
        _auditSink
            .Setup(s => s.RecordAsync(It.IsAny<AuditEntry>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEntry, CancellationToken>((e, _) => _audited.Add(e))
            .Returns(ValueTask.CompletedTask);

        _handler = new RefreshAgentRegistryCommandHandler(
            _refresher.Object,
            _auditSink.Object,
            _timeProvider,
            NullLogger<RefreshAgentRegistryCommandHandler>.Instance);
    }

    private static RefreshAgentRegistryCommand Command() => new() { CallerId = "ops@contoso.com" };

    private static AgentRegistryRefreshResult Summary() => new()
    {
        Added = ["beta"],
        Updated = [],
        Removed = ["gamma"],
        TotalAgentCount = 3,
        SearchedPaths = ["/agents"],
    };

    [Fact]
    public async Task Handle_RefreshSucceeds_ReturnsTheSummary()
    {
        _refresher.Setup(r => r.Refresh()).Returns(Summary());

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(Summary());
    }

    [Fact]
    public async Task Handle_RefreshSucceeds_RecordsAnAttemptAndAnOutcomeEntry()
    {
        _refresher.Setup(r => r.Refresh()).Returns(Summary());

        await _handler.Handle(Command(), CancellationToken.None);

        _audited.Should().HaveCount(2, "one attempt record before the work and one outcome record after");
        _audited.Should().OnlyContain(e => e.ExecutorId == "ops@contoso.com");
        _audited.Should().Contain(e => e.Metadata!["phase"] == "attempt");
        var outcome = _audited.Single(e => e.Metadata!["phase"] == "outcome");
        outcome.Outcome.Should().Be(AuditOutcome.Success);
        outcome.Metadata!["added"].Should().Be("1");
        outcome.Metadata!["removed"].Should().Be("1");
        outcome.Metadata!["total"].Should().Be("3");
    }

    [Fact]
    public async Task Handle_RefresherThrows_ReturnsFailureAndRecordsAFailureOutcome()
    {
        _refresher.Setup(r => r.Refresh()).Throws(new InvalidOperationException("boom"));

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        _audited.Should().Contain(e =>
            e.Metadata!["phase"] == "outcome" && e.Outcome == AuditOutcome.Failure);
    }

    [Fact]
    public async Task Handle_AuditSinkThrowsOnAttempt_StillRefreshesAndSucceeds()
    {
        // Fail-open, deliberately: unlike drift recalculation, a refresh is trivially repeatable and
        // re-anchors nothing security-sensitive, so an audit outage must not take the operational
        // refresh capability down with it.
        _auditSink
            .Setup(s => s.RecordAsync(It.IsAny<AuditEntry>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("sink down"));
        _refresher.Setup(r => r.Refresh()).Returns(Summary());

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _refresher.Verify(r => r.Refresh(), Times.Once);
    }
}
