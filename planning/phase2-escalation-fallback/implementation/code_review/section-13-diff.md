diff --git a/src/Content/Infrastructure/Infrastructure.AI/Resilience/PollyProviderHealthMonitor.cs b/src/Content/Infrastructure/Infrastructure.AI/Resilience/PollyProviderHealthMonitor.cs
new file mode 100644
index 0000000..3143b76
--- /dev/null
+++ b/src/Content/Infrastructure/Infrastructure.AI/Resilience/PollyProviderHealthMonitor.cs
@@ -0,0 +1,92 @@
+using System.Collections.Concurrent;
+using Application.AI.Common.Interfaces.Resilience;
+using Application.AI.Common.OpenTelemetry.Metrics;
+using Domain.AI.Resilience;
+using Domain.AI.Telemetry.Conventions;
+using Microsoft.Extensions.Logging;
+
+namespace Infrastructure.AI.Resilience;
+
+/// <summary>
+/// Bridges Polly v8 circuit breaker state to the domain <see cref="ProviderHealthState"/> enum.
+/// Exposes per-provider health through <see cref="IProviderHealthMonitor"/> and fires
+/// state-change callbacks for OTel gauge updates and retry queue drain triggers.
+/// </summary>
+/// <remarks>
+/// <para>
+/// Does not perform synthetic pre-warm probes. Recovery detection relies on Polly's
+/// built-in half-open behavior: when a circuit transitions to HalfOpen, the next real
+/// request serves as the recovery probe.
+/// </para>
+/// <para>
+/// State transitions are reported by Polly callbacks (OnOpened, OnClosed, OnHalfOpen)
+/// registered in <see cref="ProviderResiliencePipelineBuilder"/> during pipeline construction.
+/// </para>
+/// </remarks>
+public sealed class PollyProviderHealthMonitor : IProviderHealthMonitor
+{
+    private readonly ConcurrentDictionary<string, ProviderHealthState> _providerStates = new();
+    private readonly ILogger<PollyProviderHealthMonitor>? _logger;
+
+    /// <summary>Creates a new health monitor instance.</summary>
+    /// <param name="logger">Optional logger for state transition logging.</param>
+    public PollyProviderHealthMonitor(ILogger<PollyProviderHealthMonitor>? logger)
+    {
+        _logger = logger;
+    }
+
+    /// <inheritdoc/>
+    public event Action<string, ProviderHealthState>? OnCircuitStateChanged;
+
+    /// <inheritdoc/>
+    public ProviderHealthState GetProviderHealth(string providerName)
+    {
+        return _providerStates.TryGetValue(providerName, out var state) ? state : ProviderHealthState.Healthy;
+    }
+
+    /// <inheritdoc/>
+    public IReadOnlyDictionary<string, ProviderHealthState> GetAllProviderHealth()
+    {
+        return new Dictionary<string, ProviderHealthState>(_providerStates);
+    }
+
+    /// <inheritdoc/>
+    public bool IsAnyProviderHealthy()
+    {
+        if (_providerStates.IsEmpty)
+            return true;
+
+        return _providerStates.Values.Any(s => s == ProviderHealthState.Healthy);
+    }
+
+    /// <summary>
+    /// Called by Polly pipeline callbacks (OnOpened, OnClosed, OnHalfOpen) to report
+    /// circuit breaker state transitions. Fires <see cref="OnCircuitStateChanged"/>
+    /// only when the state actually changes.
+    /// </summary>
+    /// <param name="providerName">The provider whose circuit state changed.</param>
+    /// <param name="newState">The new <see cref="ProviderHealthState"/>.</param>
+    public void ReportStateChange(string providerName, ProviderHealthState newState)
+    {
+        var oldState = _providerStates.GetOrAdd(providerName, ProviderHealthState.Healthy);
+
+        if (oldState == newState)
+            return;
+
+        _providerStates[providerName] = newState;
+
+        _logger?.LogInformation("Provider {Provider} circuit state changed: {OldState} -> {NewState}",
+            providerName, oldState, newState);
+
+        ResilienceMetrics.CircuitStateChanges.Add(1,
+            new KeyValuePair<string, object?>(ResilienceConventions.ProviderName, providerName),
+            new KeyValuePair<string, object?>(ResilienceConventions.TransitionFrom, oldState.ToString()),
+            new KeyValuePair<string, object?>(ResilienceConventions.TransitionTo, newState.ToString()));
+
+        ResilienceMetrics.CircuitState.Add(
+            (long)newState - (long)oldState,
+            new KeyValuePair<string, object?>(ResilienceConventions.ProviderName, providerName));
+
+        OnCircuitStateChanged?.Invoke(providerName, newState);
+    }
+}
diff --git a/src/Content/Tests/Infrastructure.AI.Tests/Resilience/PollyProviderHealthMonitorTests.cs b/src/Content/Tests/Infrastructure.AI.Tests/Resilience/PollyProviderHealthMonitorTests.cs
new file mode 100644
index 0000000..f192b44
--- /dev/null
+++ b/src/Content/Tests/Infrastructure.AI.Tests/Resilience/PollyProviderHealthMonitorTests.cs
@@ -0,0 +1,114 @@
+using Domain.AI.Resilience;
+using FluentAssertions;
+using Infrastructure.AI.Resilience;
+using Xunit;
+
+namespace Infrastructure.AI.Tests.Resilience;
+
+/// <summary>
+/// Tests for <see cref="PollyProviderHealthMonitor"/> — verifies Polly circuit state
+/// to domain ProviderHealthState mapping, event firing, and aggregate queries.
+/// </summary>
+public sealed class PollyProviderHealthMonitorTests
+{
+    private readonly PollyProviderHealthMonitor _sut = new(null);
+
+    [Fact]
+    public void GetProviderHealth_NoStateReported_ReturnsHealthy()
+    {
+        _sut.ReportStateChange("provider-a", ProviderHealthState.Healthy);
+
+        _sut.GetProviderHealth("provider-a").Should().Be(ProviderHealthState.Healthy);
+    }
+
+    [Fact]
+    public void GetProviderHealth_Degraded_ReturnsDegraded()
+    {
+        _sut.ReportStateChange("provider-a", ProviderHealthState.Degraded);
+
+        _sut.GetProviderHealth("provider-a").Should().Be(ProviderHealthState.Degraded);
+    }
+
+    [Fact]
+    public void GetProviderHealth_Unavailable_ReturnsUnavailable()
+    {
+        _sut.ReportStateChange("provider-a", ProviderHealthState.Unavailable);
+
+        _sut.GetProviderHealth("provider-a").Should().Be(ProviderHealthState.Unavailable);
+    }
+
+    [Fact]
+    public void GetProviderHealth_UnknownProvider_ReturnsHealthy()
+    {
+        _sut.GetProviderHealth("unknown-provider").Should().Be(ProviderHealthState.Healthy);
+    }
+
+    [Fact]
+    public void GetAllProviderHealth_ReturnsAllProviders()
+    {
+        _sut.ReportStateChange("a", ProviderHealthState.Healthy);
+        _sut.ReportStateChange("b", ProviderHealthState.Degraded);
+        _sut.ReportStateChange("c", ProviderHealthState.Unavailable);
+
+        var result = _sut.GetAllProviderHealth();
+
+        result.Should().HaveCount(3);
+        result["a"].Should().Be(ProviderHealthState.Healthy);
+        result["b"].Should().Be(ProviderHealthState.Degraded);
+        result["c"].Should().Be(ProviderHealthState.Unavailable);
+    }
+
+    [Fact]
+    public void IsAnyProviderHealthy_AllUnavailable_ReturnsFalse()
+    {
+        _sut.ReportStateChange("a", ProviderHealthState.Unavailable);
+        _sut.ReportStateChange("b", ProviderHealthState.Unavailable);
+
+        _sut.IsAnyProviderHealthy().Should().BeFalse();
+    }
+
+    [Fact]
+    public void IsAnyProviderHealthy_OneHealthy_ReturnsTrue()
+    {
+        _sut.ReportStateChange("a", ProviderHealthState.Unavailable);
+        _sut.ReportStateChange("b", ProviderHealthState.Healthy);
+
+        _sut.IsAnyProviderHealthy().Should().BeTrue();
+    }
+
+    [Fact]
+    public void IsAnyProviderHealthy_NoProviders_ReturnsTrue()
+    {
+        _sut.IsAnyProviderHealthy().Should().BeTrue();
+    }
+
+    [Fact]
+    public void OnCircuitStateChanged_Fires_OnTransition()
+    {
+        string? receivedProvider = null;
+        ProviderHealthState? receivedState = null;
+        _sut.OnCircuitStateChanged += (provider, state) =>
+        {
+            receivedProvider = provider;
+            receivedState = state;
+        };
+
+        _sut.ReportStateChange("provider-a", ProviderHealthState.Unavailable);
+
+        receivedProvider.Should().Be("provider-a");
+        receivedState.Should().Be(ProviderHealthState.Unavailable);
+    }
+
+    [Fact]
+    public void OnCircuitStateChanged_DoesNotFire_WhenStateUnchanged()
+    {
+        _sut.ReportStateChange("provider-a", ProviderHealthState.Unavailable);
+
+        var callCount = 0;
+        _sut.OnCircuitStateChanged += (_, _) => callCount++;
+
+        _sut.ReportStateChange("provider-a", ProviderHealthState.Unavailable);
+
+        callCount.Should().Be(0);
+    }
+}
