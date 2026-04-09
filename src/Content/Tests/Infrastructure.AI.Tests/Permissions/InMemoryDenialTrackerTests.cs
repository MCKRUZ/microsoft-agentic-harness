using Domain.AI.Permissions;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Permissions;
using FluentAssertions;
using Infrastructure.AI.Permissions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Permissions;

public sealed class InMemoryDenialTrackerTests
{
    private const int DefaultThreshold = 3;
    private const string AgentId = "agent-1";
    private const string ToolName = "bash";

    private readonly InMemoryDenialTracker _tracker;

    public InMemoryDenialTrackerTests()
    {
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                Permissions = new PermissionsConfig
                {
                    DenialRateLimitThreshold = DefaultThreshold
                }
            }
        };

        var optionsMonitor = new Mock<IOptionsMonitor<AppConfig>>();
        optionsMonitor.Setup(o => o.CurrentValue).Returns(appConfig);

        _tracker = new InMemoryDenialTracker(
            optionsMonitor.Object,
            Mock.Of<ILogger<InMemoryDenialTracker>>());
    }

    [Fact]
    public void RecordDenial_IncrementsCount()
    {
        _tracker.RecordDenial(AgentId, ToolName);
        _tracker.RecordDenial(AgentId, ToolName);

        var denials = _tracker.GetDenials(AgentId);

        denials.Should().ContainSingle()
            .Which.DenialCount.Should().Be(2);
    }

    [Fact]
    public void IsRateLimited_BelowThreshold_ReturnsFalse()
    {
        _tracker.RecordDenial(AgentId, ToolName);
        _tracker.RecordDenial(AgentId, ToolName);

        _tracker.IsRateLimited(AgentId, ToolName).Should().BeFalse();
    }

    [Fact]
    public void IsRateLimited_AtThreshold_ReturnsTrue()
    {
        for (var i = 0; i < DefaultThreshold; i++)
            _tracker.RecordDenial(AgentId, ToolName);

        _tracker.IsRateLimited(AgentId, ToolName).Should().BeTrue();
    }

    [Fact]
    public void IsRateLimited_DifferentOperations_TrackedSeparately()
    {
        for (var i = 0; i < DefaultThreshold; i++)
            _tracker.RecordDenial(AgentId, ToolName, "read");

        _tracker.IsRateLimited(AgentId, ToolName, "read").Should().BeTrue();
        _tracker.IsRateLimited(AgentId, ToolName, "write").Should().BeFalse();
        _tracker.IsRateLimited(AgentId, ToolName).Should().BeFalse();
    }

    [Fact]
    public void GetDenials_ReturnsAllRecords()
    {
        _tracker.RecordDenial(AgentId, "bash");
        _tracker.RecordDenial(AgentId, "bash");
        _tracker.RecordDenial(AgentId, "file_system", "write");

        var denials = _tracker.GetDenials(AgentId);

        denials.Should().HaveCount(2);
        denials.Should().Contain(d => d.ToolName == "bash" && d.DenialCount == 2);
        denials.Should().Contain(d => d.ToolName == "file_system" && d.OperationPattern == "write" && d.DenialCount == 1);
    }

    [Fact]
    public void Reset_ClearsAllRecords()
    {
        for (var i = 0; i < DefaultThreshold; i++)
            _tracker.RecordDenial(AgentId, ToolName);

        _tracker.Reset(AgentId);

        _tracker.IsRateLimited(AgentId, ToolName).Should().BeFalse();
        _tracker.GetDenials(AgentId).Should().BeEmpty();
    }

    [Fact]
    public void NullOperation_TrackedAsWildcard()
    {
        _tracker.RecordDenial(AgentId, ToolName, null);
        _tracker.RecordDenial(AgentId, ToolName, null);

        var denials = _tracker.GetDenials(AgentId);

        denials.Should().ContainSingle();
        denials[0].OperationPattern.Should().BeNull();
        denials[0].DenialCount.Should().Be(2);
    }

    [Fact]
    public void IsRateLimited_UnknownAgent_ReturnsFalse()
    {
        _tracker.IsRateLimited("unknown-agent", ToolName).Should().BeFalse();
    }

    [Fact]
    public void GetDenials_UnknownAgent_ReturnsEmptyList()
    {
        _tracker.GetDenials("unknown-agent").Should().BeEmpty();
    }

    [Fact]
    public void RecordDenial_SetsTimestamps()
    {
        var before = DateTimeOffset.UtcNow;
        _tracker.RecordDenial(AgentId, ToolName);
        var after = DateTimeOffset.UtcNow;

        var denial = _tracker.GetDenials(AgentId).Single();

        denial.FirstDenied.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        denial.LastDenied.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }
}
