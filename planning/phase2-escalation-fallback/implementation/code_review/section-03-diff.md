diff --git a/src/Content/Application/Application.AI.Common/OpenTelemetry/Metrics/EscalationMetrics.cs b/src/Content/Application/Application.AI.Common/OpenTelemetry/Metrics/EscalationMetrics.cs
new file mode 100644
index 0000000..40e54c4
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/OpenTelemetry/Metrics/EscalationMetrics.cs
@@ -0,0 +1,41 @@
+using System.Diagnostics.Metrics;
+using Domain.AI.Telemetry.Conventions;
+using Domain.Common.Telemetry;
+
+namespace Application.AI.Common.OpenTelemetry.Metrics;
+
+/// <summary>
+/// OTel metric instruments for tracking escalation request lifecycle.
+/// Records request volume, resolution outcomes, latency distributions,
+/// and pending escalation depth.
+/// </summary>
+/// <remarks>
+/// Recorded by <c>DefaultEscalationService</c> (Section 08) on request creation,
+/// resolution, timeout, and per-approver decision submission.
+/// </remarks>
+public static class EscalationMetrics
+{
+    /// <summary>Escalation requests created. Tags: agent_id, tool, priority.</summary>
+    public static Counter<long> Requests { get; } =
+        AppInstrument.Meter.CreateCounter<long>(EscalationConventions.Requests, "{request}", "Escalation requests created");
+
+    /// <summary>Escalation resolutions. Tags: resolution_type, priority.</summary>
+    public static Counter<long> Resolutions { get; } =
+        AppInstrument.Meter.CreateCounter<long>(EscalationConventions.Resolutions, "{resolution}", "Escalation resolutions");
+
+    /// <summary>Escalation request-to-resolution duration. Tags: priority.</summary>
+    public static Histogram<double> DurationMs { get; } =
+        AppInstrument.Meter.CreateHistogram<double>(EscalationConventions.DurationMs, "ms", "Escalation request-to-resolution duration");
+
+    /// <summary>Escalation timeout events. Tags: priority.</summary>
+    public static Counter<long> Timeouts { get; } =
+        AppInstrument.Meter.CreateCounter<long>(EscalationConventions.Timeouts, "{timeout}", "Escalation timeout events");
+
+    /// <summary>Currently pending escalations (inc on request, dec on resolution).</summary>
+    public static UpDownCounter<long> Pending { get; } =
+        AppInstrument.Meter.CreateUpDownCounter<long>(EscalationConventions.Pending, "{escalation}", "Currently pending escalations");
+
+    /// <summary>Per-approver response latency. Tags: approver.</summary>
+    public static Histogram<double> ApproverResponseMs { get; } =
+        AppInstrument.Meter.CreateHistogram<double>(EscalationConventions.ApproverResponseMs, "ms", "Per-approver response latency");
+}
diff --git a/src/Content/Application/Application.AI.Common/OpenTelemetry/Metrics/ResilienceMetrics.cs b/src/Content/Application/Application.AI.Common/OpenTelemetry/Metrics/ResilienceMetrics.cs
new file mode 100644
index 0000000..40162da
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/OpenTelemetry/Metrics/ResilienceMetrics.cs
@@ -0,0 +1,53 @@
+using System.Diagnostics.Metrics;
+using Domain.AI.Telemetry.Conventions;
+using Domain.Common.Telemetry;
+
+namespace Application.AI.Common.OpenTelemetry.Metrics;
+
+/// <summary>
+/// OTel metric instruments for tracking provider resilience — circuit breaker state,
+/// fallback activations, retry attempts, and retry queue health.
+/// </summary>
+/// <remarks>
+/// <para>Recorded by multiple downstream sections:</para>
+/// <list type="bullet">
+///   <item><c>ProviderResiliencePipelineBuilder</c> (Section 11) — retry attempts, provider duration</item>
+///   <item><c>ResilientChatClient</c> (Section 12) — fallback activations, degradation events</item>
+///   <item><c>PollyProviderHealthMonitor</c> (Section 13) — circuit state changes, circuit state gauge</item>
+///   <item><c>LlmRetryQueue</c> (Section 15) — queue size, queue expired</item>
+/// </list>
+/// </remarks>
+public static class ResilienceMetrics
+{
+    /// <summary>Fallback provider activations. Tags: provider.</summary>
+    public static Counter<long> FallbackActivations { get; } =
+        AppInstrument.Meter.CreateCounter<long>(ResilienceConventions.FallbackActivations, "{activation}", "Fallback provider activations");
+
+    /// <summary>Circuit breaker state transitions. Tags: provider, from, to.</summary>
+    public static Counter<long> CircuitStateChanges { get; } =
+        AppInstrument.Meter.CreateCounter<long>(ResilienceConventions.CircuitStateChanges, "{change}", "Circuit breaker state transitions");
+
+    /// <summary>Per-provider circuit state gauge (0=healthy, 1=degraded, 2=unavailable). Tags: provider.</summary>
+    public static UpDownCounter<long> CircuitState { get; } =
+        AppInstrument.Meter.CreateUpDownCounter<long>(ResilienceConventions.CircuitState, "{state}", "Per-provider circuit state gauge");
+
+    /// <summary>Retry attempts per provider. Tags: provider.</summary>
+    public static Counter<long> RetryAttempts { get; } =
+        AppInstrument.Meter.CreateCounter<long>(ResilienceConventions.RetryAttempts, "{attempt}", "Retry attempts per provider");
+
+    /// <summary>Per-provider request duration. Tags: provider.</summary>
+    public static Histogram<double> ProviderDurationMs { get; } =
+        AppInstrument.Meter.CreateHistogram<double>(ResilienceConventions.ProviderDurationMs, "ms", "Per-provider request duration");
+
+    /// <summary>Full provider exhaustion events (all providers failed).</summary>
+    public static Counter<long> DegradationEvents { get; } =
+        AppInstrument.Meter.CreateCounter<long>(ResilienceConventions.DegradationEvents, "{event}", "Full provider exhaustion events");
+
+    /// <summary>Retry queue depth gauge. Tags: provider.</summary>
+    public static UpDownCounter<long> QueueSize { get; } =
+        AppInstrument.Meter.CreateUpDownCounter<long>(ResilienceConventions.QueueSize, "{request}", "Retry queue depth gauge");
+
+    /// <summary>TTL-expired requests removed from the retry queue.</summary>
+    public static Counter<long> QueueExpired { get; } =
+        AppInstrument.Meter.CreateCounter<long>(ResilienceConventions.QueueExpired, "{expiry}", "TTL-expired queued requests");
+}
diff --git a/src/Content/Domain/Domain.AI/Telemetry/Conventions/EscalationConventions.cs b/src/Content/Domain/Domain.AI/Telemetry/Conventions/EscalationConventions.cs
new file mode 100644
index 0000000..05a8e21
--- /dev/null
+++ b/src/Content/Domain/Domain.AI/Telemetry/Conventions/EscalationConventions.cs
@@ -0,0 +1,79 @@
+namespace Domain.AI.Telemetry.Conventions;
+
+/// <summary>
+/// OTel attribute names and metric identifiers for the escalation subsystem.
+/// Used throughout Phase 2 to tag spans, structured logs, and metric recordings
+/// with consistent, discoverable dimension keys.
+/// </summary>
+public static class EscalationConventions
+{
+    // ── Attribute name constants (span/log attribute keys) ──
+
+    /// <summary>Unique escalation identifier for cross-service correlation.</summary>
+    public const string EscalationId = "agent.escalation.id";
+
+    /// <summary>The agent that triggered the escalation request.</summary>
+    public const string AgentId = "agent.escalation.agent_id";
+
+    /// <summary>The tool invocation that required escalation approval.</summary>
+    public const string ToolName = "agent.escalation.tool";
+
+    /// <summary>Escalation priority level (informational, blocking, critical).</summary>
+    public const string Priority = "agent.escalation.priority";
+
+    /// <summary>How the escalation was resolved (approved, denied, timed_out, escalated).</summary>
+    public const string ResolutionType = "agent.escalation.resolution_type";
+
+    /// <summary>Approval strategy used (any_of, all_of, quorum).</summary>
+    public const string Strategy = "agent.escalation.strategy";
+
+    /// <summary>Individual approver identifier for per-approver tracking.</summary>
+    public const string ApproverName = "agent.escalation.approver";
+
+    // ── Metric identifier constants (instrument names) ──
+
+    /// <summary>Counter of escalation requests created. Tags: agent_id, tool, priority.</summary>
+    public const string Requests = "agent.escalation.requests";
+
+    /// <summary>Counter of resolved escalations. Tags: resolution_type, priority.</summary>
+    public const string Resolutions = "agent.escalation.resolutions";
+
+    /// <summary>Histogram of time from escalation request to resolution, in milliseconds.</summary>
+    public const string DurationMs = "agent.escalation.duration_ms";
+
+    /// <summary>Counter of escalations that exceeded their timeout window.</summary>
+    public const string Timeouts = "agent.escalation.timeouts";
+
+    /// <summary>Gauge of currently active pending escalations (inc on request, dec on resolution).</summary>
+    public const string Pending = "agent.escalation.pending";
+
+    /// <summary>Histogram of individual approver response latency in milliseconds. Tags: approver.</summary>
+    public const string ApproverResponseMs = "agent.escalation.approver_response_ms";
+
+    // ── Well-known tag value classes ──
+
+    /// <summary>Well-known values for the <see cref="Priority"/> attribute.</summary>
+    public static class PriorityValues
+    {
+        public const string Informational = "informational";
+        public const string Blocking = "blocking";
+        public const string Critical = "critical";
+    }
+
+    /// <summary>Well-known values for the <see cref="ResolutionType"/> attribute.</summary>
+    public static class ResolutionValues
+    {
+        public const string Approved = "approved";
+        public const string Denied = "denied";
+        public const string TimedOut = "timed_out";
+        public const string Escalated = "escalated";
+    }
+
+    /// <summary>Well-known values for the <see cref="Strategy"/> attribute.</summary>
+    public static class StrategyValues
+    {
+        public const string AnyOf = "any_of";
+        public const string AllOf = "all_of";
+        public const string Quorum = "quorum";
+    }
+}
diff --git a/src/Content/Domain/Domain.AI/Telemetry/Conventions/ResilienceConventions.cs b/src/Content/Domain/Domain.AI/Telemetry/Conventions/ResilienceConventions.cs
new file mode 100644
index 0000000..12bd648
--- /dev/null
+++ b/src/Content/Domain/Domain.AI/Telemetry/Conventions/ResilienceConventions.cs
@@ -0,0 +1,62 @@
+namespace Domain.AI.Telemetry.Conventions;
+
+/// <summary>
+/// OTel attribute names and metric identifiers for the resilience subsystem.
+/// Covers circuit breaker state, fallback activations, retry tracking, and
+/// retry queue metrics used by the provider health and fallback infrastructure.
+/// </summary>
+public static class ResilienceConventions
+{
+    // ── Attribute name constants (span/log attribute keys) ──
+
+    /// <summary>Provider identifier for per-provider metric tagging.</summary>
+    public const string ProviderName = "agent.resilience.provider";
+
+    /// <summary>Circuit breaker state name (healthy, degraded, unavailable).</summary>
+    public const string CircuitStateName = "agent.resilience.circuit.state_name";
+
+    /// <summary>State transition source in circuit breaker state change events.</summary>
+    public const string TransitionFrom = "agent.resilience.circuit.from";
+
+    /// <summary>State transition target in circuit breaker state change events.</summary>
+    public const string TransitionTo = "agent.resilience.circuit.to";
+
+    /// <summary>Comma-separated list of provider names that failed during a fallback chain.</summary>
+    public const string FailedProviders = "agent.resilience.failed_providers";
+
+    // ── Metric identifier constants (instrument names) ──
+
+    /// <summary>Counter of fallback provider activations. Tags: provider.</summary>
+    public const string FallbackActivations = "agent.resilience.fallback.activations";
+
+    /// <summary>Counter of circuit breaker state transitions. Tags: provider, from, to.</summary>
+    public const string CircuitStateChanges = "agent.resilience.circuit.state_changes";
+
+    /// <summary>Gauge of per-provider circuit state (0=healthy, 1=degraded, 2=unavailable). Tags: provider.</summary>
+    public const string CircuitState = "agent.resilience.circuit.state";
+
+    /// <summary>Counter of retry attempts per provider. Tags: provider.</summary>
+    public const string RetryAttempts = "agent.resilience.retry.attempts";
+
+    /// <summary>Histogram of per-provider request duration in milliseconds. Tags: provider.</summary>
+    public const string ProviderDurationMs = "agent.resilience.provider.duration_ms";
+
+    /// <summary>Counter of full provider exhaustion events (all providers failed).</summary>
+    public const string DegradationEvents = "agent.resilience.degradation.events";
+
+    /// <summary>Gauge of retry queue depth. Tags: provider.</summary>
+    public const string QueueSize = "agent.resilience.queue.size";
+
+    /// <summary>Counter of TTL-expired requests removed from the retry queue.</summary>
+    public const string QueueExpired = "agent.resilience.queue.expired";
+
+    // ── Well-known tag value classes ──
+
+    /// <summary>Well-known values for the <see cref="CircuitStateName"/> attribute.</summary>
+    public static class HealthValues
+    {
+        public const string Healthy = "healthy";
+        public const string Degraded = "degraded";
+        public const string Unavailable = "unavailable";
+    }
+}
diff --git a/src/Content/Tests/Application.AI.Common.Tests/OpenTelemetry/Metrics/MetricsInstrumentTests.cs b/src/Content/Tests/Application.AI.Common.Tests/OpenTelemetry/Metrics/MetricsInstrumentTests.cs
index 3579f4f..efc7708 100644
--- a/src/Content/Tests/Application.AI.Common.Tests/OpenTelemetry/Metrics/MetricsInstrumentTests.cs
+++ b/src/Content/Tests/Application.AI.Common.Tests/OpenTelemetry/Metrics/MetricsInstrumentTests.cs
@@ -1,4 +1,5 @@
 using Application.AI.Common.OpenTelemetry.Metrics;
+using Domain.AI.Telemetry.Conventions;
 using FluentAssertions;
 using Xunit;
 
@@ -116,4 +117,132 @@ public class MetricsInstrumentTests
         var second = ToolExecutionMetrics.Duration;
         first.Should().BeSameAs(second);
     }
+
+    // ── Escalation Metrics ──
+
+    [Fact]
+    public void EscalationMetrics_Requests_IsNotNull()
+    {
+        EscalationMetrics.Requests.Should().NotBeNull();
+        EscalationMetrics.Requests.Name.Should().Be(EscalationConventions.Requests);
+    }
+
+    [Fact]
+    public void EscalationMetrics_Resolutions_IsNotNull()
+    {
+        EscalationMetrics.Resolutions.Should().NotBeNull();
+        EscalationMetrics.Resolutions.Name.Should().Be(EscalationConventions.Resolutions);
+    }
+
+    [Fact]
+    public void EscalationMetrics_DurationMs_IsNotNull()
+    {
+        EscalationMetrics.DurationMs.Should().NotBeNull();
+        EscalationMetrics.DurationMs.Name.Should().Be(EscalationConventions.DurationMs);
+    }
+
+    [Fact]
+    public void EscalationMetrics_Timeouts_IsNotNull()
+    {
+        EscalationMetrics.Timeouts.Should().NotBeNull();
+        EscalationMetrics.Timeouts.Name.Should().Be(EscalationConventions.Timeouts);
+    }
+
+    [Fact]
+    public void EscalationMetrics_Pending_IsNotNull()
+    {
+        EscalationMetrics.Pending.Should().NotBeNull();
+        EscalationMetrics.Pending.Name.Should().Be(EscalationConventions.Pending);
+    }
+
+    [Fact]
+    public void EscalationMetrics_ApproverResponseMs_IsNotNull()
+    {
+        EscalationMetrics.ApproverResponseMs.Should().NotBeNull();
+        EscalationMetrics.ApproverResponseMs.Name.Should().Be(EscalationConventions.ApproverResponseMs);
+    }
+
+    // ── Resilience Metrics ──
+
+    [Fact]
+    public void ResilienceMetrics_FallbackActivations_IsNotNull()
+    {
+        ResilienceMetrics.FallbackActivations.Should().NotBeNull();
+        ResilienceMetrics.FallbackActivations.Name.Should().Be(ResilienceConventions.FallbackActivations);
+    }
+
+    [Fact]
+    public void ResilienceMetrics_CircuitStateChanges_IsNotNull()
+    {
+        ResilienceMetrics.CircuitStateChanges.Should().NotBeNull();
+        ResilienceMetrics.CircuitStateChanges.Name.Should().Be(ResilienceConventions.CircuitStateChanges);
+    }
+
+    [Fact]
+    public void ResilienceMetrics_CircuitState_IsNotNull()
+    {
+        ResilienceMetrics.CircuitState.Should().NotBeNull();
+        ResilienceMetrics.CircuitState.Name.Should().Be(ResilienceConventions.CircuitState);
+    }
+
+    [Fact]
+    public void ResilienceMetrics_RetryAttempts_IsNotNull()
+    {
+        ResilienceMetrics.RetryAttempts.Should().NotBeNull();
+        ResilienceMetrics.RetryAttempts.Name.Should().Be(ResilienceConventions.RetryAttempts);
+    }
+
+    [Fact]
+    public void ResilienceMetrics_ProviderDurationMs_IsNotNull()
+    {
+        ResilienceMetrics.ProviderDurationMs.Should().NotBeNull();
+        ResilienceMetrics.ProviderDurationMs.Name.Should().Be(ResilienceConventions.ProviderDurationMs);
+    }
+
+    [Fact]
+    public void ResilienceMetrics_DegradationEvents_IsNotNull()
+    {
+        ResilienceMetrics.DegradationEvents.Should().NotBeNull();
+        ResilienceMetrics.DegradationEvents.Name.Should().Be(ResilienceConventions.DegradationEvents);
+    }
+
+    [Fact]
+    public void ResilienceMetrics_QueueSize_IsNotNull()
+    {
+        ResilienceMetrics.QueueSize.Should().NotBeNull();
+        ResilienceMetrics.QueueSize.Name.Should().Be(ResilienceConventions.QueueSize);
+    }
+
+    [Fact]
+    public void ResilienceMetrics_QueueExpired_IsNotNull()
+    {
+        ResilienceMetrics.QueueExpired.Should().NotBeNull();
+        ResilienceMetrics.QueueExpired.Name.Should().Be(ResilienceConventions.QueueExpired);
+    }
+
+    // ── Conventions Naming Convention Tests ──
+
+    [Fact]
+    public void EscalationConventions_Constants_FollowNamingConvention()
+    {
+        EscalationConventions.Requests.Should().StartWith("agent.escalation.");
+        EscalationConventions.Resolutions.Should().StartWith("agent.escalation.");
+        EscalationConventions.DurationMs.Should().StartWith("agent.escalation.");
+        EscalationConventions.Timeouts.Should().StartWith("agent.escalation.");
+        EscalationConventions.Pending.Should().StartWith("agent.escalation.");
+        EscalationConventions.ApproverResponseMs.Should().StartWith("agent.escalation.");
+    }
+
+    [Fact]
+    public void ResilienceConventions_Constants_FollowNamingConvention()
+    {
+        ResilienceConventions.FallbackActivations.Should().StartWith("agent.resilience.");
+        ResilienceConventions.CircuitStateChanges.Should().StartWith("agent.resilience.");
+        ResilienceConventions.CircuitState.Should().StartWith("agent.resilience.");
+        ResilienceConventions.RetryAttempts.Should().StartWith("agent.resilience.");
+        ResilienceConventions.ProviderDurationMs.Should().StartWith("agent.resilience.");
+        ResilienceConventions.DegradationEvents.Should().StartWith("agent.resilience.");
+        ResilienceConventions.QueueSize.Should().StartWith("agent.resilience.");
+        ResilienceConventions.QueueExpired.Should().StartWith("agent.resilience.");
+    }
 }
