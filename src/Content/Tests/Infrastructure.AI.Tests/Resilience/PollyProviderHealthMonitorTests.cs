using Domain.AI.Resilience;
using FluentAssertions;
using Infrastructure.AI.Resilience;
using Xunit;

namespace Infrastructure.AI.Tests.Resilience;

/// <summary>
/// Tests for <see cref="PollyProviderHealthMonitor"/> — verifies Polly circuit state
/// to domain ProviderHealthState mapping, event firing, and aggregate queries.
/// </summary>
public sealed class PollyProviderHealthMonitorTests
{
    private readonly PollyProviderHealthMonitor _sut = new(null);

    [Fact]
    public void GetProviderHealth_ExplicitlyReportedHealthy_ReturnsHealthy()
    {
        _sut.ReportStateChange("provider-a", ProviderHealthState.Healthy);

        _sut.GetProviderHealth("provider-a").Should().Be(ProviderHealthState.Healthy);
    }

    [Fact]
    public void GetProviderHealth_Degraded_ReturnsDegraded()
    {
        _sut.ReportStateChange("provider-a", ProviderHealthState.Degraded);

        _sut.GetProviderHealth("provider-a").Should().Be(ProviderHealthState.Degraded);
    }

    [Fact]
    public void GetProviderHealth_Unavailable_ReturnsUnavailable()
    {
        _sut.ReportStateChange("provider-a", ProviderHealthState.Unavailable);

        _sut.GetProviderHealth("provider-a").Should().Be(ProviderHealthState.Unavailable);
    }

    [Fact]
    public void GetProviderHealth_UnknownProvider_ReturnsHealthy()
    {
        _sut.GetProviderHealth("unknown-provider").Should().Be(ProviderHealthState.Healthy);
    }

    [Fact]
    public void GetAllProviderHealth_ReturnsAllProviders()
    {
        _sut.ReportStateChange("a", ProviderHealthState.Healthy);
        _sut.ReportStateChange("b", ProviderHealthState.Degraded);
        _sut.ReportStateChange("c", ProviderHealthState.Unavailable);

        var result = _sut.GetAllProviderHealth();

        result.Should().HaveCount(3);
        result["a"].Should().Be(ProviderHealthState.Healthy);
        result["b"].Should().Be(ProviderHealthState.Degraded);
        result["c"].Should().Be(ProviderHealthState.Unavailable);
    }

    [Fact]
    public void IsAnyProviderHealthy_AllUnavailable_ReturnsFalse()
    {
        _sut.ReportStateChange("a", ProviderHealthState.Unavailable);
        _sut.ReportStateChange("b", ProviderHealthState.Unavailable);

        _sut.IsAnyProviderHealthy().Should().BeFalse();
    }

    [Fact]
    public void IsAnyProviderHealthy_OneHealthy_ReturnsTrue()
    {
        _sut.ReportStateChange("a", ProviderHealthState.Unavailable);
        _sut.ReportStateChange("b", ProviderHealthState.Healthy);

        _sut.IsAnyProviderHealthy().Should().BeTrue();
    }

    [Fact]
    public void IsAnyProviderHealthy_NoProviders_ReturnsTrue()
    {
        _sut.IsAnyProviderHealthy().Should().BeTrue();
    }

    [Fact]
    public void OnCircuitStateChanged_Fires_OnTransition()
    {
        string? receivedProvider = null;
        ProviderHealthState? receivedState = null;
        _sut.OnCircuitStateChanged += (provider, state) =>
        {
            receivedProvider = provider;
            receivedState = state;
        };

        _sut.ReportStateChange("provider-a", ProviderHealthState.Unavailable);

        receivedProvider.Should().Be("provider-a");
        receivedState.Should().Be(ProviderHealthState.Unavailable);
    }

    [Fact]
    public void OnCircuitStateChanged_DoesNotFire_WhenStateUnchanged()
    {
        _sut.ReportStateChange("provider-a", ProviderHealthState.Unavailable);

        var callCount = 0;
        _sut.OnCircuitStateChanged += (_, _) => callCount++;

        _sut.ReportStateChange("provider-a", ProviderHealthState.Unavailable);

        callCount.Should().Be(0);
    }

    [Fact]
    public void ReportStateChange_ConcurrentCalls_FiresEventExactlyOnce()
    {
        var fireCount = 0;
        _sut.OnCircuitStateChanged += (_, _) => Interlocked.Increment(ref fireCount);

        Parallel.For(0, 100, _ =>
            _sut.ReportStateChange("provider-a", ProviderHealthState.Unavailable));

        fireCount.Should().Be(1);
    }
}
