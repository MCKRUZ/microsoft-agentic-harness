using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.ContextManagement;
using FluentAssertions;
using Infrastructure.AI.Compaction;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Compaction;

public sealed class AutoCompactStateMachineTests
{
    private readonly AppConfig _appConfig;
    private readonly AutoCompactStateMachine _sut;

    public AutoCompactStateMachineTests()
    {
        _appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                ContextManagement = new ContextManagementConfig
                {
                    Compaction = new CompactionConfig
                    {
                        CircuitBreakerMaxFailures = 3,
                        CircuitBreakerCooldownSeconds = 60
                    }
                }
            }
        };

        var options = Mock.Of<IOptionsMonitor<AppConfig>>(o => o.CurrentValue == _appConfig);
        _sut = new AutoCompactStateMachine(options);
    }

    [Fact]
    public void RecordSuccess_ResetsFailureCount()
    {
        _sut.RecordFailure("agent-1");
        _sut.RecordFailure("agent-1");
        _sut.GetConsecutiveFailures("agent-1").Should().Be(2);

        _sut.RecordSuccess("agent-1");

        _sut.GetConsecutiveFailures("agent-1").Should().Be(0);
    }

    [Fact]
    public void RecordFailure_IncrementsCount()
    {
        _sut.RecordFailure("agent-1");
        _sut.GetConsecutiveFailures("agent-1").Should().Be(1);

        _sut.RecordFailure("agent-1");
        _sut.GetConsecutiveFailures("agent-1").Should().Be(2);

        _sut.RecordFailure("agent-1");
        _sut.GetConsecutiveFailures("agent-1").Should().Be(3);
    }

    [Fact]
    public void IsCircuitBroken_BelowThreshold_ReturnsFalse()
    {
        _sut.RecordFailure("agent-1");
        _sut.RecordFailure("agent-1");

        // 2 failures < 3 threshold
        _sut.IsCircuitBroken("agent-1").Should().BeFalse();
    }

    [Fact]
    public void IsCircuitBroken_AtThreshold_ReturnsTrue()
    {
        _sut.RecordFailure("agent-1");
        _sut.RecordFailure("agent-1");
        _sut.RecordFailure("agent-1");

        // 3 failures == 3 threshold, within cooldown window
        _sut.IsCircuitBroken("agent-1").Should().BeTrue();
    }

    [Fact]
    public void IsCircuitBroken_AfterCooldown_ReturnsFalse()
    {
        // Use a very short cooldown to test expiration
        _appConfig.AI.ContextManagement.Compaction.CircuitBreakerCooldownSeconds = 0;

        _sut.RecordFailure("agent-1");
        _sut.RecordFailure("agent-1");
        _sut.RecordFailure("agent-1");

        // Cooldown is 0 seconds, so the circuit should reset immediately
        _sut.IsCircuitBroken("agent-1").Should().BeFalse();
    }
}
