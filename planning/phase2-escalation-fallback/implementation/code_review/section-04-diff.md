diff --git a/src/Content/Application/Application.Core/Validation/EscalationConfigValidator.cs b/src/Content/Application/Application.Core/Validation/EscalationConfigValidator.cs
new file mode 100644
index 0000000..eec70d7
--- /dev/null
+++ b/src/Content/Application/Application.Core/Validation/EscalationConfigValidator.cs
@@ -0,0 +1,46 @@
+using Domain.AI.Escalation;
+using Domain.Common.Config.AI.Governance;
+using FluentValidation;
+
+namespace Application.Core.Validation;
+
+/// <summary>
+/// Validates <see cref="EscalationConfig"/> ensuring timeouts are non-negative,
+/// priority levels are configured when enabled, and enum string values are valid.
+/// </summary>
+public sealed class EscalationConfigValidator : AbstractValidator<EscalationConfig>
+{
+    private static readonly string[] ValidTimeoutActions =
+        Enum.GetNames<EscalationTimeoutAction>();
+
+    private static readonly string[] ValidApprovalStrategies =
+        Enum.GetNames<ApprovalStrategyType>();
+
+    public EscalationConfigValidator()
+    {
+        RuleFor(x => x.DefaultTimeoutSeconds)
+            .GreaterThanOrEqualTo(0)
+            .WithMessage("DefaultTimeoutSeconds must be >= 0 (zero is valid for informational-only).");
+
+        RuleFor(x => x.DefaultTimeoutAction)
+            .Must(v => ValidTimeoutActions.Contains(v))
+            .WithMessage($"DefaultTimeoutAction must be one of: {string.Join(", ", ValidTimeoutActions)}.");
+
+        RuleFor(x => x.DefaultApprovalStrategy)
+            .Must(v => ValidApprovalStrategies.Contains(v))
+            .WithMessage($"DefaultApprovalStrategy must be one of: {string.Join(", ", ValidApprovalStrategies)}.");
+
+        RuleFor(x => x.PriorityLevels)
+            .NotEmpty()
+            .WithMessage("PriorityLevels must be configured when escalation is enabled.")
+            .When(x => x.Enabled);
+
+        RuleForEach(x => x.PriorityLevels)
+            .ChildRules(entry =>
+            {
+                entry.RuleFor(kv => kv.Value.TimeoutSeconds)
+                    .GreaterThanOrEqualTo(0)
+                    .WithMessage("PriorityLevels[{PropertyName}].TimeoutSeconds must be >= 0.");
+            });
+    }
+}
diff --git a/src/Content/Application/Application.Core/Validation/ResilienceConfigValidator.cs b/src/Content/Application/Application.Core/Validation/ResilienceConfigValidator.cs
new file mode 100644
index 0000000..4d60b79
--- /dev/null
+++ b/src/Content/Application/Application.Core/Validation/ResilienceConfigValidator.cs
@@ -0,0 +1,68 @@
+using Domain.Common.Config.AI.Resilience;
+using FluentValidation;
+
+namespace Application.Core.Validation;
+
+/// <summary>
+/// Validates <see cref="ResilienceConfig"/> ensuring fallback chain is populated when enabled,
+/// circuit breaker ratios are in range, and all numeric tuning values are positive.
+/// </summary>
+public sealed class ResilienceConfigValidator : AbstractValidator<ResilienceConfig>
+{
+    public ResilienceConfigValidator()
+    {
+        RuleFor(x => x.FallbackChain)
+            .NotEmpty()
+            .WithMessage("FallbackChain must contain at least one provider when resilience is enabled.")
+            .When(x => x.Enabled);
+
+        RuleForEach(x => x.FallbackChain)
+            .ChildRules(entry =>
+            {
+                entry.RuleFor(p => p.DeploymentId)
+                    .NotEmpty()
+                    .WithMessage("Each FallbackChain entry must have a DeploymentId.");
+
+                entry.RuleFor(p => p.ClientType)
+                    .IsInEnum()
+                    .WithMessage("Each FallbackChain entry must have a valid ClientType.");
+            });
+
+        RuleFor(x => x.CircuitBreaker.FailureRatio)
+            .GreaterThan(0)
+            .LessThan(1)
+            .WithMessage("CircuitBreaker.FailureRatio must be between 0 and 1 exclusive.");
+
+        RuleFor(x => x.CircuitBreaker.SamplingDurationSeconds)
+            .GreaterThan(0)
+            .WithMessage("CircuitBreaker.SamplingDurationSeconds must be > 0.");
+
+        RuleFor(x => x.CircuitBreaker.MinimumThroughput)
+            .GreaterThan(0)
+            .WithMessage("CircuitBreaker.MinimumThroughput must be > 0.");
+
+        RuleFor(x => x.CircuitBreaker.BreakDurationSeconds)
+            .GreaterThan(0)
+            .WithMessage("CircuitBreaker.BreakDurationSeconds must be > 0.");
+
+        RuleFor(x => x.Retry.MaxAttempts)
+            .GreaterThanOrEqualTo(1)
+            .WithMessage("Retry.MaxAttempts must be >= 1.");
+
+        RuleFor(x => x.Retry.BaseDelaySeconds)
+            .GreaterThanOrEqualTo(0)
+            .WithMessage("Retry.BaseDelaySeconds must be >= 0.");
+
+        RuleFor(x => x.Timeout.PerAttemptSeconds)
+            .GreaterThan(0)
+            .WithMessage("Timeout.PerAttemptSeconds must be > 0.");
+
+        RuleFor(x => x.DegradedMode.MaxQueueSize)
+            .GreaterThan(0)
+            .WithMessage("DegradedMode.MaxQueueSize must be > 0.");
+
+        RuleFor(x => x.DegradedMode.RetryQueueTtlSeconds)
+            .GreaterThan(0)
+            .WithMessage("DegradedMode.RetryQueueTtlSeconds must be > 0.");
+    }
+}
diff --git a/src/Content/Domain/Domain.Common/Config/AI/AIConfig.cs b/src/Content/Domain/Domain.Common/Config/AI/AIConfig.cs
index dcf9d02..f2ad581 100644
--- a/src/Content/Domain/Domain.Common/Config/AI/AIConfig.cs
+++ b/src/Content/Domain/Domain.Common/Config/AI/AIConfig.cs
@@ -6,6 +6,7 @@ using Domain.Common.Config.AI.MCP;
 using Domain.Common.Config.AI.Orchestration;
 using Domain.Common.Config.AI.Permissions;
 using Domain.Common.Config.AI.RAG;
+using Domain.Common.Config.AI.Resilience;
 
 namespace Domain.Common.Config.AI;
 
@@ -27,6 +28,7 @@ namespace Domain.Common.Config.AI;
 /// ├── Permissions       — Permission system for tool and file access approvals
 /// ├── Hooks             — Lifecycle hook execution configuration
 /// ├── Orchestration     — Subagent management and streaming execution
+/// ├── Resilience        — LLM fallback chains, circuit breakers, retry, degraded mode
 /// └── Rag               — RAG pipeline: ingestion, retrieval, reranking, model tiering
 /// </code>
 /// </para>
@@ -99,6 +101,12 @@ public class AIConfig
     /// </summary>
     public RagConfig Rag { get; set; } = new();
 
+    /// <summary>
+    /// LLM provider resilience configuration including fallback chains,
+    /// circuit breakers, retry policies, and degraded mode behavior.
+    /// </summary>
+    public ResilienceConfig Resilience { get; set; } = new();
+
     /// <summary>Agent Governance Toolkit configuration.</summary>
     public GovernanceConfig Governance { get; init; } = new();
 }
diff --git a/src/Content/Domain/Domain.Common/Config/AI/Governance/EscalationConfig.cs b/src/Content/Domain/Domain.Common/Config/AI/Governance/EscalationConfig.cs
new file mode 100644
index 0000000..a3a6309
--- /dev/null
+++ b/src/Content/Domain/Domain.Common/Config/AI/Governance/EscalationConfig.cs
@@ -0,0 +1,56 @@
+namespace Domain.Common.Config.AI.Governance;
+
+/// <summary>
+/// Root configuration for the human escalation subsystem.
+/// Bound from <c>AppConfig:AI:Governance:Escalation</c> in appsettings.json.
+/// </summary>
+/// <remarks>
+/// <para>
+/// Configuration hierarchy:
+/// <code>
+/// AppConfig.AI.Governance.Escalation
+/// ├── Enabled                  — Master toggle for escalation
+/// ├── DefaultTimeoutSeconds    — Global escalation timeout
+/// ├── DefaultTimeoutAction     — Deny / DenyAndEscalate / Approve / Escalate
+/// ├── DefaultApprovalStrategy  — AnyOf / AllOf / Quorum
+/// └── PriorityLevels{}         — Per-priority overrides keyed by EscalationPriority name
+///     ├── TimeoutSeconds       — Override timeout for this level
+///     ├── Async                — Non-blocking mode (informational)
+///     └── EscalateToAll        — Notify all approvers simultaneously (critical)
+/// </code>
+/// </para>
+/// </remarks>
+public class EscalationConfig
+{
+    /// <summary>
+    /// Whether the escalation system is active. When disabled,
+    /// <c>GovernancePolicyBehavior</c> treats <c>RequireApproval</c> as a denial.
+    /// </summary>
+    public bool Enabled { get; set; }
+
+    /// <summary>
+    /// How long (in seconds) to wait for approver responses before firing the timeout action.
+    /// Zero is valid for informational-only escalations.
+    /// </summary>
+    public int DefaultTimeoutSeconds { get; set; } = 300;
+
+    /// <summary>
+    /// Action taken when escalation times out. String value of <c>EscalationTimeoutAction</c>
+    /// enum: "Deny", "DenyAndEscalate", "Approve", "Escalate".
+    /// Validated at the Application layer.
+    /// </summary>
+    public string DefaultTimeoutAction { get; set; } = "DenyAndEscalate";
+
+    /// <summary>
+    /// Default approval strategy when a governance rule does not specify one.
+    /// String value of <c>ApprovalStrategyType</c> enum: "AnyOf", "AllOf", "Quorum".
+    /// Validated at the Application layer.
+    /// </summary>
+    public string DefaultApprovalStrategy { get; set; } = "AnyOf";
+
+    /// <summary>
+    /// Per-priority-level overrides keyed by <c>EscalationPriority</c> name
+    /// ("Informational", "Blocking", "Critical").
+    /// </summary>
+    public Dictionary<string, EscalationPriorityConfig> PriorityLevels { get; set; } = new();
+}
diff --git a/src/Content/Domain/Domain.Common/Config/AI/Governance/EscalationPriorityConfig.cs b/src/Content/Domain/Domain.Common/Config/AI/Governance/EscalationPriorityConfig.cs
new file mode 100644
index 0000000..2b1f29c
--- /dev/null
+++ b/src/Content/Domain/Domain.Common/Config/AI/Governance/EscalationPriorityConfig.cs
@@ -0,0 +1,23 @@
+namespace Domain.Common.Config.AI.Governance;
+
+/// <summary>
+/// Per-priority-level overrides for escalation behavior.
+/// Each entry in <see cref="EscalationConfig.PriorityLevels"/> maps to one of these.
+/// </summary>
+public class EscalationPriorityConfig
+{
+    /// <summary>Override timeout (in seconds) for this priority level.</summary>
+    public int TimeoutSeconds { get; set; } = 300;
+
+    /// <summary>
+    /// When true, escalation is non-blocking (informational only).
+    /// The agent continues processing while the escalation resolves asynchronously.
+    /// </summary>
+    public bool Async { get; set; }
+
+    /// <summary>
+    /// When true, all approvers are notified simultaneously regardless of strategy ordering.
+    /// Typically used for <c>Critical</c> priority.
+    /// </summary>
+    public bool EscalateToAll { get; set; }
+}
diff --git a/src/Content/Domain/Domain.Common/Config/AI/GovernanceConfig.cs b/src/Content/Domain/Domain.Common/Config/AI/GovernanceConfig.cs
index cdafd72..4b7cc35 100644
--- a/src/Content/Domain/Domain.Common/Config/AI/GovernanceConfig.cs
+++ b/src/Content/Domain/Domain.Common/Config/AI/GovernanceConfig.cs
@@ -1,3 +1,5 @@
+using Domain.Common.Config.AI.Governance;
+
 namespace Domain.Common.Config.AI;
 
 /// <summary>
@@ -43,4 +45,10 @@ public sealed class GovernanceConfig
     /// Findings below this level are redacted and the sanitized response continues.
     /// </summary>
     public ThreatLevel ResponseBlockThreshold { get; init; } = ThreatLevel.Critical;
+
+    /// <summary>
+    /// Human escalation configuration for approval workflows triggered when
+    /// agents exceed their authority.
+    /// </summary>
+    public EscalationConfig Escalation { get; set; } = new();
 }
diff --git a/src/Content/Domain/Domain.Common/Config/AI/Resilience/CircuitBreakerConfig.cs b/src/Content/Domain/Domain.Common/Config/AI/Resilience/CircuitBreakerConfig.cs
new file mode 100644
index 0000000..cd7d066
--- /dev/null
+++ b/src/Content/Domain/Domain.Common/Config/AI/Resilience/CircuitBreakerConfig.cs
@@ -0,0 +1,19 @@
+namespace Domain.Common.Config.AI.Resilience;
+
+/// <summary>
+/// Tuning for Polly v8 ratio-based circuit breaker applied to each LLM provider.
+/// </summary>
+public class CircuitBreakerConfig
+{
+    /// <summary>Failure ratio threshold (0 &lt; value &lt; 1) to trip the circuit.</summary>
+    public double FailureRatio { get; set; } = 0.5;
+
+    /// <summary>Sliding window size in seconds for failure ratio sampling.</summary>
+    public int SamplingDurationSeconds { get; set; } = 30;
+
+    /// <summary>Minimum requests in the sampling window before the circuit evaluates.</summary>
+    public int MinimumThroughput { get; set; } = 5;
+
+    /// <summary>How long (in seconds) the circuit stays open before allowing a probe.</summary>
+    public int BreakDurationSeconds { get; set; } = 60;
+}
diff --git a/src/Content/Domain/Domain.Common/Config/AI/Resilience/DegradedModeConfig.cs b/src/Content/Domain/Domain.Common/Config/AI/Resilience/DegradedModeConfig.cs
new file mode 100644
index 0000000..c3d5347
--- /dev/null
+++ b/src/Content/Domain/Domain.Common/Config/AI/Resilience/DegradedModeConfig.cs
@@ -0,0 +1,14 @@
+namespace Domain.Common.Config.AI.Resilience;
+
+/// <summary>
+/// Configuration for the retry queue and degraded mode behavior
+/// when all LLM providers are exhausted.
+/// </summary>
+public class DegradedModeConfig
+{
+    /// <summary>How long (in seconds) queued requests survive before TTL expiry.</summary>
+    public int RetryQueueTtlSeconds { get; set; } = 300;
+
+    /// <summary>Maximum items in the retry queue.</summary>
+    public int MaxQueueSize { get; set; } = 100;
+}
diff --git a/src/Content/Domain/Domain.Common/Config/AI/Resilience/FallbackProviderConfig.cs b/src/Content/Domain/Domain.Common/Config/AI/Resilience/FallbackProviderConfig.cs
new file mode 100644
index 0000000..bb77071
--- /dev/null
+++ b/src/Content/Domain/Domain.Common/Config/AI/Resilience/FallbackProviderConfig.cs
@@ -0,0 +1,19 @@
+namespace Domain.Common.Config.AI.Resilience;
+
+/// <summary>
+/// One entry in the resilience fallback chain. Maps directly to
+/// <c>IChatClientFactory.GetChatClientAsync(clientType, deploymentId)</c>.
+/// </summary>
+public class FallbackProviderConfig
+{
+    /// <summary>
+    /// Which provider SDK to use for this fallback entry.
+    /// </summary>
+    public AIAgentFrameworkClientType ClientType { get; set; } = AIAgentFrameworkClientType.AzureOpenAI;
+
+    /// <summary>Model deployment name passed to the chat client factory.</summary>
+    public string DeploymentId { get; set; } = "";
+
+    /// <summary>Optional feature declarations for capability diffing.</summary>
+    public ProviderCapabilitiesConfig Capabilities { get; set; } = new();
+}
diff --git a/src/Content/Domain/Domain.Common/Config/AI/Resilience/ProviderCapabilitiesConfig.cs b/src/Content/Domain/Domain.Common/Config/AI/Resilience/ProviderCapabilitiesConfig.cs
new file mode 100644
index 0000000..966c554
--- /dev/null
+++ b/src/Content/Domain/Domain.Common/Config/AI/Resilience/ProviderCapabilitiesConfig.cs
@@ -0,0 +1,23 @@
+namespace Domain.Common.Config.AI.Resilience;
+
+/// <summary>
+/// Declares what an LLM provider supports. Used by <c>ProviderCapabilityRegistry</c>
+/// (Section 14) for capability diffing when falling back to a less capable provider.
+/// </summary>
+public class ProviderCapabilitiesConfig
+{
+    /// <summary>Whether the provider supports tool/function calling.</summary>
+    public bool SupportsToolCalling { get; set; } = true;
+
+    /// <summary>Whether the provider supports streaming responses.</summary>
+    public bool SupportsStreaming { get; set; } = true;
+
+    /// <summary>Whether the provider supports vision/image inputs.</summary>
+    public bool SupportsVision { get; set; }
+
+    /// <summary>Maximum tokens the provider can generate in a single response.</summary>
+    public int MaxTokens { get; set; } = 4096;
+
+    /// <summary>Media types the provider accepts (e.g., "image/png", "image/jpeg").</summary>
+    public IReadOnlyList<string> SupportedMediaTypes { get; set; } = [];
+}
diff --git a/src/Content/Domain/Domain.Common/Config/AI/Resilience/ResilienceConfig.cs b/src/Content/Domain/Domain.Common/Config/AI/Resilience/ResilienceConfig.cs
new file mode 100644
index 0000000..f2fa49a
--- /dev/null
+++ b/src/Content/Domain/Domain.Common/Config/AI/Resilience/ResilienceConfig.cs
@@ -0,0 +1,50 @@
+namespace Domain.Common.Config.AI.Resilience;
+
+/// <summary>
+/// Root configuration for LLM provider resilience including fallback chains,
+/// circuit breakers, retry policies, and degraded mode behavior.
+/// Bound from <c>AppConfig:AI:Resilience</c> in appsettings.json.
+/// </summary>
+/// <remarks>
+/// <para>
+/// Configuration hierarchy:
+/// <code>
+/// AppConfig.AI.Resilience
+/// ├── Enabled          — Master toggle for resilience
+/// ├── FallbackChain[]  — Ordered provider entries with capabilities
+/// │   ├── ClientType       — Provider SDK (AzureOpenAI, OpenAI, etc.)
+/// │   ├── DeploymentId     — Model deployment name
+/// │   └── Capabilities     — Feature declarations (tool calling, streaming, vision)
+/// ├── CircuitBreaker   — Failure ratio, sampling, break duration
+/// ├── Retry            — Max attempts, backoff
+/// ├── Timeout          — Per-attempt timeout
+/// └── DegradedMode     — Retry queue TTL and max size
+/// </code>
+/// </para>
+/// </remarks>
+public class ResilienceConfig
+{
+    /// <summary>
+    /// Master toggle. When disabled, <c>ResilientChatClientProvider</c> returns
+    /// the primary provider's raw client and <c>LlmRetryQueue</c> is not registered.
+    /// </summary>
+    public bool Enabled { get; set; }
+
+    /// <summary>
+    /// Ordered list of LLM providers. First entry is primary; rest are fallbacks
+    /// activated in order when the primary is unavailable.
+    /// </summary>
+    public FallbackProviderConfig[] FallbackChain { get; set; } = [];
+
+    /// <summary>Circuit breaker tuning for Polly v8 ratio-based circuit breaker.</summary>
+    public CircuitBreakerConfig CircuitBreaker { get; set; } = new();
+
+    /// <summary>Retry policy tuning for transient failure handling.</summary>
+    public RetryConfig Retry { get; set; } = new();
+
+    /// <summary>Per-attempt timeout configuration.</summary>
+    public TimeoutConfig Timeout { get; set; } = new();
+
+    /// <summary>Retry queue and degraded mode behavior when all providers are exhausted.</summary>
+    public DegradedModeConfig DegradedMode { get; set; } = new();
+}
diff --git a/src/Content/Domain/Domain.Common/Config/AI/Resilience/RetryConfig.cs b/src/Content/Domain/Domain.Common/Config/AI/Resilience/RetryConfig.cs
new file mode 100644
index 0000000..6def202
--- /dev/null
+++ b/src/Content/Domain/Domain.Common/Config/AI/Resilience/RetryConfig.cs
@@ -0,0 +1,16 @@
+namespace Domain.Common.Config.AI.Resilience;
+
+/// <summary>
+/// Retry policy tuning for transient LLM provider failures.
+/// </summary>
+public class RetryConfig
+{
+    /// <summary>Maximum attempts including the initial attempt.</summary>
+    public int MaxAttempts { get; set; } = 2;
+
+    /// <summary>Base delay in seconds for exponential backoff with jitter.</summary>
+    public double BaseDelaySeconds { get; set; } = 1.0;
+
+    /// <summary>Backoff type: "Exponential" or "Linear".</summary>
+    public string BackoffType { get; set; } = "Exponential";
+}
diff --git a/src/Content/Domain/Domain.Common/Config/AI/Resilience/TimeoutConfig.cs b/src/Content/Domain/Domain.Common/Config/AI/Resilience/TimeoutConfig.cs
new file mode 100644
index 0000000..65dea3b
--- /dev/null
+++ b/src/Content/Domain/Domain.Common/Config/AI/Resilience/TimeoutConfig.cs
@@ -0,0 +1,10 @@
+namespace Domain.Common.Config.AI.Resilience;
+
+/// <summary>
+/// Per-attempt timeout configuration for LLM provider calls.
+/// </summary>
+public class TimeoutConfig
+{
+    /// <summary>Timeout in seconds for each individual provider call attempt.</summary>
+    public int PerAttemptSeconds { get; set; } = 30;
+}
diff --git a/src/Content/Tests/Application.Core.Tests/Validation/EscalationConfigValidatorTests.cs b/src/Content/Tests/Application.Core.Tests/Validation/EscalationConfigValidatorTests.cs
new file mode 100644
index 0000000..586e8da
--- /dev/null
+++ b/src/Content/Tests/Application.Core.Tests/Validation/EscalationConfigValidatorTests.cs
@@ -0,0 +1,87 @@
+using Application.Core.Validation;
+using Domain.Common.Config.AI.Governance;
+using FluentAssertions;
+using Xunit;
+
+namespace Application.Core.Tests.Validation;
+
+/// <summary>
+/// Tests for <see cref="EscalationConfigValidator"/>.
+/// Pattern: CreateValidConfig() baseline, mutate one field per test.
+/// </summary>
+public class EscalationConfigValidatorTests
+{
+    private readonly EscalationConfigValidator _validator = new();
+
+    [Fact]
+    public async Task Validate_ValidConfig_NoErrors()
+    {
+        var config = CreateValidConfig();
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeTrue();
+        result.Errors.Should().BeEmpty();
+    }
+
+    [Fact]
+    public async Task Validate_NegativeTimeout_HasError()
+    {
+        var config = CreateValidConfig();
+        config.DefaultTimeoutSeconds = -1;
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeFalse();
+        result.Errors.Should().Contain(e => e.PropertyName == "DefaultTimeoutSeconds");
+    }
+
+    [Fact]
+    public async Task Validate_ZeroTimeout_Allowed()
+    {
+        var config = CreateValidConfig();
+        config.DefaultTimeoutSeconds = 0;
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeTrue();
+    }
+
+    [Fact]
+    public async Task Validate_NegativePriorityTimeout_HasError()
+    {
+        var config = CreateValidConfig();
+        config.PriorityLevels["Blocking"].TimeoutSeconds = -5;
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeFalse();
+        result.Errors.Should().Contain(e => e.PropertyName.Contains("TimeoutSeconds"));
+    }
+
+    [Fact]
+    public async Task Validate_EmptyPriorityLevels_HasError()
+    {
+        var config = CreateValidConfig();
+        config.PriorityLevels = new Dictionary<string, EscalationPriorityConfig>();
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeFalse();
+        result.Errors.Should().Contain(e => e.PropertyName == "PriorityLevels");
+    }
+
+    private static EscalationConfig CreateValidConfig() => new()
+    {
+        Enabled = true,
+        DefaultTimeoutSeconds = 300,
+        DefaultTimeoutAction = "DenyAndEscalate",
+        DefaultApprovalStrategy = "AnyOf",
+        PriorityLevels = new Dictionary<string, EscalationPriorityConfig>
+        {
+            ["Informational"] = new() { TimeoutSeconds = 600, Async = true },
+            ["Blocking"] = new() { TimeoutSeconds = 300 },
+            ["Critical"] = new() { TimeoutSeconds = 120, EscalateToAll = true }
+        }
+    };
+}
diff --git a/src/Content/Tests/Application.Core.Tests/Validation/ResilienceConfigValidatorTests.cs b/src/Content/Tests/Application.Core.Tests/Validation/ResilienceConfigValidatorTests.cs
new file mode 100644
index 0000000..d5625f3
--- /dev/null
+++ b/src/Content/Tests/Application.Core.Tests/Validation/ResilienceConfigValidatorTests.cs
@@ -0,0 +1,173 @@
+using Application.Core.Validation;
+using Domain.Common.Config.AI;
+using Domain.Common.Config.AI.Resilience;
+using FluentAssertions;
+using Xunit;
+
+namespace Application.Core.Tests.Validation;
+
+/// <summary>
+/// Tests for <see cref="ResilienceConfigValidator"/>.
+/// Pattern: CreateValidConfig() baseline, mutate one field per test.
+/// When Enabled=false, FallbackChain can be empty. Numeric ranges always enforced.
+/// </summary>
+public class ResilienceConfigValidatorTests
+{
+    private readonly ResilienceConfigValidator _validator = new();
+
+    [Fact]
+    public async Task Validate_ValidConfig_NoErrors()
+    {
+        var config = CreateValidConfig();
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeTrue();
+        result.Errors.Should().BeEmpty();
+    }
+
+    [Fact]
+    public async Task Validate_EmptyFallbackChain_HasError()
+    {
+        var config = CreateValidConfig();
+        config.FallbackChain = [];
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeFalse();
+        result.Errors.Should().Contain(e => e.PropertyName == "FallbackChain");
+    }
+
+    [Fact]
+    public async Task Validate_NegativeFailureRatio_HasError()
+    {
+        var config = CreateValidConfig();
+        config.CircuitBreaker.FailureRatio = -0.1;
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeFalse();
+        result.Errors.Should().Contain(e => e.PropertyName == "CircuitBreaker.FailureRatio");
+    }
+
+    [Fact]
+    public async Task Validate_FailureRatioAboveOne_HasError()
+    {
+        var config = CreateValidConfig();
+        config.CircuitBreaker.FailureRatio = 1.5;
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeFalse();
+        result.Errors.Should().Contain(e => e.PropertyName == "CircuitBreaker.FailureRatio");
+    }
+
+    [Fact]
+    public async Task Validate_NegativeTimeout_HasError()
+    {
+        var config = CreateValidConfig();
+        config.Timeout.PerAttemptSeconds = -1;
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeFalse();
+        result.Errors.Should().Contain(e => e.PropertyName == "Timeout.PerAttemptSeconds");
+    }
+
+    [Fact]
+    public async Task Validate_ZeroMaxQueueSize_HasError()
+    {
+        var config = CreateValidConfig();
+        config.DegradedMode.MaxQueueSize = 0;
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeFalse();
+        result.Errors.Should().Contain(e => e.PropertyName == "DegradedMode.MaxQueueSize");
+    }
+
+    [Fact]
+    public async Task Validate_MissingDeploymentId_HasError()
+    {
+        var config = CreateValidConfig();
+        config.FallbackChain[0].DeploymentId = "";
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeFalse();
+        result.Errors.Should().Contain(e => e.PropertyName.Contains("DeploymentId"));
+    }
+
+    [Fact]
+    public async Task Validate_DisabledConfig_SkipsChainValidation()
+    {
+        var config = CreateValidConfig();
+        config.Enabled = false;
+        config.FallbackChain = [];
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeTrue();
+    }
+
+    [Fact]
+    public async Task Validate_NegativeRetryBaseDelay_HasError()
+    {
+        var config = CreateValidConfig();
+        config.Retry.BaseDelaySeconds = -1;
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeFalse();
+        result.Errors.Should().Contain(e => e.PropertyName == "Retry.BaseDelaySeconds");
+    }
+
+    [Fact]
+    public async Task Validate_ZeroMinimumThroughput_HasError()
+    {
+        var config = CreateValidConfig();
+        config.CircuitBreaker.MinimumThroughput = 0;
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeFalse();
+        result.Errors.Should().Contain(e => e.PropertyName == "CircuitBreaker.MinimumThroughput");
+    }
+
+    private static ResilienceConfig CreateValidConfig() => new()
+    {
+        Enabled = true,
+        FallbackChain =
+        [
+            new FallbackProviderConfig
+            {
+                ClientType = AIAgentFrameworkClientType.AzureOpenAI,
+                DeploymentId = "gpt-4o"
+            },
+            new FallbackProviderConfig
+            {
+                ClientType = AIAgentFrameworkClientType.Anthropic,
+                DeploymentId = "claude-sonnet-4-20250514"
+            }
+        ],
+        CircuitBreaker = new CircuitBreakerConfig
+        {
+            FailureRatio = 0.5,
+            SamplingDurationSeconds = 30,
+            MinimumThroughput = 5,
+            BreakDurationSeconds = 60
+        },
+        Retry = new RetryConfig
+        {
+            MaxAttempts = 2,
+            BaseDelaySeconds = 1.0,
+            BackoffType = "Exponential"
+        },
+        Timeout = new TimeoutConfig { PerAttemptSeconds = 30 },
+        DegradedMode = new DegradedModeConfig
+        {
+            RetryQueueTtlSeconds = 300,
+            MaxQueueSize = 100
+        }
+    };
+}
