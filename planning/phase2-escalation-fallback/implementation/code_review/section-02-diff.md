diff --git a/src/Content/Domain/Domain.AI/Resilience/FallbackMetadata.cs b/src/Content/Domain/Domain.AI/Resilience/FallbackMetadata.cs
new file mode 100644
index 0000000..1dab931
--- /dev/null
+++ b/src/Content/Domain/Domain.AI/Resilience/FallbackMetadata.cs
@@ -0,0 +1,27 @@
+namespace Domain.AI.Resilience;
+
+/// <summary>
+/// Metadata attached to a chat response indicating which provider served it
+/// and what capabilities were lost during fallback.
+/// Constructed by <c>ResilientChatClient</c> after iterating the provider chain.
+/// </summary>
+public sealed record FallbackMetadata
+{
+    /// <summary>The provider name that served the response.</summary>
+    public required string ActiveProvider { get; init; }
+
+    /// <summary>True when the response came from a non-primary provider.</summary>
+    public required bool IsFallback { get; init; }
+
+    /// <summary>Ordered list of providers that failed before <see cref="ActiveProvider"/> succeeded.</summary>
+    public required IReadOnlyList<string> FailedProviders { get; init; }
+
+    /// <summary>
+    /// Features unavailable on the active provider compared to the primary.
+    /// Populated by <c>ProviderCapabilityRegistry</c> diffing primary vs. active provider capabilities.
+    /// </summary>
+    public required IReadOnlySet<string> DisabledCapabilities { get; init; }
+
+    /// <summary>Snapshot of all providers' health at the time of the response.</summary>
+    public required IReadOnlyDictionary<string, ProviderHealthState> CircuitStates { get; init; }
+}
diff --git a/src/Content/Domain/Domain.AI/Resilience/ProviderExhaustedException.cs b/src/Content/Domain/Domain.AI/Resilience/ProviderExhaustedException.cs
new file mode 100644
index 0000000..76bd82c
--- /dev/null
+++ b/src/Content/Domain/Domain.AI/Resilience/ProviderExhaustedException.cs
@@ -0,0 +1,30 @@
+namespace Domain.AI.Resilience;
+
+/// <summary>
+/// Thrown when every provider in the fallback chain has failed.
+/// Carries structured failure information for callers to decide on retry, degraded response, or escalation.
+/// </summary>
+public sealed class ProviderExhaustedException : Exception
+{
+    /// <summary>Which providers were attempted before exhaustion.</summary>
+    public IReadOnlyList<string> FailedProviders { get; }
+
+    /// <summary>Suggested wait time before retrying, derived from the shortest circuit breaker break duration.</summary>
+    public TimeSpan RetryAfter { get; }
+
+    /// <summary>Creates a new instance with the specified failed providers and retry hint.</summary>
+    public ProviderExhaustedException(IReadOnlyList<string> failedProviders, TimeSpan retryAfter)
+        : base($"All LLM providers exhausted: {string.Join(", ", failedProviders)}. Retry after {retryAfter.TotalSeconds}s.")
+    {
+        FailedProviders = failedProviders;
+        RetryAfter = retryAfter;
+    }
+
+    /// <summary>Creates a new instance wrapping the last provider's exception.</summary>
+    public ProviderExhaustedException(IReadOnlyList<string> failedProviders, TimeSpan retryAfter, Exception innerException)
+        : base($"All LLM providers exhausted: {string.Join(", ", failedProviders)}. Retry after {retryAfter.TotalSeconds}s.", innerException)
+    {
+        FailedProviders = failedProviders;
+        RetryAfter = retryAfter;
+    }
+}
diff --git a/src/Content/Domain/Domain.AI/Resilience/ProviderHealthState.cs b/src/Content/Domain/Domain.AI/Resilience/ProviderHealthState.cs
new file mode 100644
index 0000000..92b02ce
--- /dev/null
+++ b/src/Content/Domain/Domain.AI/Resilience/ProviderHealthState.cs
@@ -0,0 +1,15 @@
+namespace Domain.AI.Resilience;
+
+/// <summary>
+/// Health state of an LLM provider, mapped from Polly circuit breaker states.
+/// Numeric ordering enables <c>&gt;=</c> comparisons for severity checks.
+/// </summary>
+public enum ProviderHealthState
+{
+    /// <summary>Provider is accepting requests normally. Maps to circuit breaker Closed state.</summary>
+    Healthy = 0,
+    /// <summary>Provider is being probed for recovery. Maps to circuit breaker HalfOpen state.</summary>
+    Degraded = 1,
+    /// <summary>Provider is not accepting requests. Maps to circuit breaker Open or Isolated state.</summary>
+    Unavailable = 2
+}
diff --git a/src/Content/Tests/Domain.AI.Tests/Resilience/ResilienceDomainModelTests.cs b/src/Content/Tests/Domain.AI.Tests/Resilience/ResilienceDomainModelTests.cs
new file mode 100644
index 0000000..8c1d825
--- /dev/null
+++ b/src/Content/Tests/Domain.AI.Tests/Resilience/ResilienceDomainModelTests.cs
@@ -0,0 +1,100 @@
+using Domain.AI.Resilience;
+using Xunit;
+
+namespace Domain.AI.Tests.Resilience;
+
+/// <summary>
+/// Tests for resilience domain records, enums, and exception behavior.
+/// </summary>
+public sealed class ResilienceDomainModelTests
+{
+    [Fact]
+    public void FallbackMetadata_NoFallback_IsFallbackFalse()
+    {
+        var metadata = new FallbackMetadata
+        {
+            ActiveProvider = "primary",
+            IsFallback = false,
+            FailedProviders = [],
+            DisabledCapabilities = new HashSet<string>(),
+            CircuitStates = new Dictionary<string, ProviderHealthState>
+            {
+                ["primary"] = ProviderHealthState.Healthy
+            }
+        };
+
+        Assert.False(metadata.IsFallback);
+        Assert.Empty(metadata.FailedProviders);
+    }
+
+    [Fact]
+    public void FallbackMetadata_WithFallback_IsFallbackTrue()
+    {
+        var metadata = new FallbackMetadata
+        {
+            ActiveProvider = "secondary",
+            IsFallback = true,
+            FailedProviders = ["primary"],
+            DisabledCapabilities = new HashSet<string>(),
+            CircuitStates = new Dictionary<string, ProviderHealthState>
+            {
+                ["primary"] = ProviderHealthState.Unavailable,
+                ["secondary"] = ProviderHealthState.Healthy
+            }
+        };
+
+        Assert.True(metadata.IsFallback);
+        Assert.Contains("primary", metadata.FailedProviders);
+    }
+
+    [Fact]
+    public void FallbackMetadata_DisabledCapabilities_ReflectsProviderDiff()
+    {
+        var metadata = new FallbackMetadata
+        {
+            ActiveProvider = "fallback-provider",
+            IsFallback = true,
+            FailedProviders = ["primary"],
+            DisabledCapabilities = new HashSet<string> { "vision", "streaming" },
+            CircuitStates = new Dictionary<string, ProviderHealthState>()
+        };
+
+        Assert.Contains("vision", metadata.DisabledCapabilities);
+        Assert.Contains("streaming", metadata.DisabledCapabilities);
+        Assert.Equal(2, metadata.DisabledCapabilities.Count);
+    }
+
+    [Fact]
+    public void ProviderExhaustedException_ContainsRetryAfterAndFailedProviders()
+    {
+        var failedProviders = new[] { "azure-openai", "anthropic" };
+        var retryAfter = TimeSpan.FromSeconds(60);
+
+        var exception = new ProviderExhaustedException(failedProviders, retryAfter);
+
+        Assert.Equal(retryAfter, exception.RetryAfter);
+        Assert.Equal(2, exception.FailedProviders.Count);
+        Assert.Contains("azure-openai", exception.FailedProviders);
+        Assert.Contains("anthropic", exception.FailedProviders);
+        Assert.Contains("azure-openai", exception.Message);
+        Assert.Contains("anthropic", exception.Message);
+    }
+
+    [Fact]
+    public void ProviderExhaustedException_WithInnerException_WrapsCorrectly()
+    {
+        var inner = new InvalidOperationException("Rate limited");
+        var exception = new ProviderExhaustedException(
+            ["azure-openai"], TimeSpan.FromSeconds(30), inner);
+
+        Assert.Same(inner, exception.InnerException);
+        Assert.Contains("azure-openai", exception.Message);
+    }
+
+    [Fact]
+    public void ProviderHealthState_NumericOrdering_EnablesComparison()
+    {
+        Assert.True(ProviderHealthState.Healthy < ProviderHealthState.Degraded);
+        Assert.True(ProviderHealthState.Degraded < ProviderHealthState.Unavailable);
+    }
+}
