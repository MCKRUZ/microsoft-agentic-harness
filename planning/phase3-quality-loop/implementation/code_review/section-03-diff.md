diff --git a/src/Content/Application/Application.Core/Validation/DriftDetectionConfigValidator.cs b/src/Content/Application/Application.Core/Validation/DriftDetectionConfigValidator.cs
new file mode 100644
index 0000000..920e056
--- /dev/null
+++ b/src/Content/Application/Application.Core/Validation/DriftDetectionConfigValidator.cs
@@ -0,0 +1,43 @@
+using Domain.Common.Config.AI.DriftDetection;
+using FluentValidation;
+
+namespace Application.Core.Validation;
+
+/// <summary>
+/// Validates <see cref="DriftDetectionConfig"/> constraints including
+/// EWMA parameter ranges and threshold ordering invariant (Warn &lt; Alert &lt; Escalate).
+/// </summary>
+public sealed class DriftDetectionConfigValidator : AbstractValidator<DriftDetectionConfig>
+{
+    public DriftDetectionConfigValidator()
+    {
+        RuleFor(x => x.EwmaLambda)
+            .GreaterThan(0).WithMessage("EwmaLambda must be > 0.")
+            .LessThanOrEqualTo(1).WithMessage("EwmaLambda must be <= 1.");
+
+        RuleFor(x => x.ControlLimitWidth)
+            .GreaterThan(0).WithMessage("ControlLimitWidth must be > 0.");
+
+        RuleFor(x => x.MinSamplesForBaseline)
+            .GreaterThan(0).WithMessage("MinSamplesForBaseline must be > 0.");
+
+        RuleFor(x => x.BaselineWindowDays)
+            .GreaterThan(0).WithMessage("BaselineWindowDays must be > 0.");
+
+        RuleFor(x => x.WarnThresholdSigma)
+            .GreaterThan(0)
+            .LessThan(x => x.AlertThresholdSigma)
+            .WithMessage("WarnThresholdSigma must be less than AlertThresholdSigma.");
+
+        RuleFor(x => x.AlertThresholdSigma)
+            .GreaterThan(0)
+            .LessThan(x => x.EscalateThresholdSigma)
+            .WithMessage("AlertThresholdSigma must be less than EscalateThresholdSigma.");
+
+        RuleFor(x => x.EscalateThresholdSigma)
+            .GreaterThan(0);
+
+        RuleFor(x => x.AuditPath)
+            .NotEmpty().WithMessage("AuditPath must be configured.");
+    }
+}
diff --git a/src/Content/Domain/Domain.Common/Config/AI/AIConfig.cs b/src/Content/Domain/Domain.Common/Config/AI/AIConfig.cs
index f2ad581..3535ea2 100644
--- a/src/Content/Domain/Domain.Common/Config/AI/AIConfig.cs
+++ b/src/Content/Domain/Domain.Common/Config/AI/AIConfig.cs
@@ -6,6 +6,7 @@ using Domain.Common.Config.AI.MCP;
 using Domain.Common.Config.AI.Orchestration;
 using Domain.Common.Config.AI.Permissions;
 using Domain.Common.Config.AI.RAG;
+using Domain.Common.Config.AI.DriftDetection;
 using Domain.Common.Config.AI.Resilience;
 
 namespace Domain.Common.Config.AI;
@@ -29,7 +30,8 @@ namespace Domain.Common.Config.AI;
 /// ├── Hooks             — Lifecycle hook execution configuration
 /// ├── Orchestration     — Subagent management and streaming execution
 /// ├── Resilience        — LLM fallback chains, circuit breakers, retry, degraded mode
-/// └── Rag               — RAG pipeline: ingestion, retrieval, reranking, model tiering
+/// ├── Rag               — RAG pipeline: ingestion, retrieval, reranking, model tiering
+/// └── DriftDetection    — EWMA-based drift detection for quality regressions
 /// </code>
 /// </para>
 /// </remarks>
@@ -107,6 +109,11 @@ public class AIConfig
     /// </summary>
     public ResilienceConfig Resilience { get; set; } = new();
 
+    /// <summary>
+    /// EWMA-based drift detection configuration for identifying quality regressions.
+    /// </summary>
+    public DriftDetectionConfig DriftDetection { get; set; } = new();
+
     /// <summary>Agent Governance Toolkit configuration.</summary>
     public GovernanceConfig Governance { get; init; } = new();
 }
diff --git a/src/Content/Domain/Domain.Common/Config/AI/DriftDetection/DriftDetectionConfig.cs b/src/Content/Domain/Domain.Common/Config/AI/DriftDetection/DriftDetectionConfig.cs
new file mode 100644
index 0000000..06f524d
--- /dev/null
+++ b/src/Content/Domain/Domain.Common/Config/AI/DriftDetection/DriftDetectionConfig.cs
@@ -0,0 +1,92 @@
+namespace Domain.Common.Config.AI.DriftDetection;
+
+/// <summary>
+/// Configuration for the EWMA-based drift detection subsystem.
+/// Bound from <c>AppConfig:AI:DriftDetection</c> in appsettings.json.
+/// </summary>
+/// <remarks>
+/// Configuration hierarchy:
+/// <code>
+/// AppConfig.AI.DriftDetection
+/// +-- Enabled                -- Master toggle for drift detection
+/// +-- EwmaLambda             -- EWMA smoothing factor (0, 1]
+/// +-- ControlLimitWidth      -- Sigma multiplier for control limits
+/// +-- MinSamplesForBaseline  -- Minimum evaluations before baseline is valid
+/// +-- BaselineWindowDays     -- Rolling window for baseline recalculation
+/// +-- WarnThresholdSigma     -- Deviation triggering Warn severity
+/// +-- AlertThresholdSigma    -- Deviation triggering Alert severity
+/// +-- EscalateThresholdSigma -- Deviation triggering Escalate severity
+/// +-- EscalationEnabled      -- Whether Escalate severity triggers Phase 2 escalation
+/// +-- AuditPath              -- Directory for JSONL drift audit files
+/// </code>
+/// Threshold ordering invariant: Warn &lt; Alert &lt; Escalate.
+/// Enforced by <c>DriftDetectionConfigValidator</c>.
+/// </remarks>
+public class DriftDetectionConfig
+{
+    /// <summary>
+    /// Master toggle. When disabled, <c>DefaultDriftDetectionService</c>
+    /// returns <c>Result.Success</c> with default/empty values for all operations.
+    /// </summary>
+    public bool Enabled { get; set; } = true;
+
+    /// <summary>
+    /// EWMA smoothing factor. Higher values weight recent observations more heavily.
+    /// Must be in range (0, 1].
+    /// </summary>
+    /// <value>Default: 0.2</value>
+    public double EwmaLambda { get; set; } = 0.2;
+
+    /// <summary>
+    /// Sigma multiplier for EWMA control limits (UCL/LCL).
+    /// UCL = baseline_mean + L * sigma * sqrt(lambda / (2 - lambda)).
+    /// </summary>
+    /// <value>Default: 3.0</value>
+    public double ControlLimitWidth { get; set; } = 3.0;
+
+    /// <summary>
+    /// Minimum number of evaluations required before a baseline is considered valid.
+    /// </summary>
+    /// <value>Default: 20</value>
+    public int MinSamplesForBaseline { get; set; } = 20;
+
+    /// <summary>
+    /// Rolling window in days for baseline recalculation.
+    /// </summary>
+    /// <value>Default: 7</value>
+    public int BaselineWindowDays { get; set; } = 7;
+
+    /// <summary>
+    /// Sigma deviation threshold for <c>DriftSeverity.Warn</c>.
+    /// Must be less than <see cref="AlertThresholdSigma"/>.
+    /// </summary>
+    /// <value>Default: 1.5</value>
+    public double WarnThresholdSigma { get; set; } = 1.5;
+
+    /// <summary>
+    /// Sigma deviation threshold for <c>DriftSeverity.Alert</c>.
+    /// Must be between <see cref="WarnThresholdSigma"/> and <see cref="EscalateThresholdSigma"/>.
+    /// </summary>
+    /// <value>Default: 2.5</value>
+    public double AlertThresholdSigma { get; set; } = 2.5;
+
+    /// <summary>
+    /// Sigma deviation threshold for <c>DriftSeverity.Escalate</c>.
+    /// Must be greater than <see cref="AlertThresholdSigma"/>.
+    /// </summary>
+    /// <value>Default: 3.0</value>
+    public double EscalateThresholdSigma { get; set; } = 3.0;
+
+    /// <summary>
+    /// Whether drift events at <c>DriftSeverity.Escalate</c> trigger the
+    /// Phase 2 human escalation system via <c>IEscalationService</c>.
+    /// </summary>
+    /// <value>Default: true</value>
+    public bool EscalationEnabled { get; set; } = true;
+
+    /// <summary>
+    /// Directory path for the JSONL drift audit store.
+    /// </summary>
+    /// <value>Default: "data/audit"</value>
+    public string AuditPath { get; set; } = "data/audit";
+}
diff --git a/src/Content/Tests/Application.Core.Tests/Validation/DriftDetectionConfigValidatorTests.cs b/src/Content/Tests/Application.Core.Tests/Validation/DriftDetectionConfigValidatorTests.cs
new file mode 100644
index 0000000..ba97f5f
--- /dev/null
+++ b/src/Content/Tests/Application.Core.Tests/Validation/DriftDetectionConfigValidatorTests.cs
@@ -0,0 +1,216 @@
+using Application.Core.Validation;
+using Domain.Common.Config.AI.DriftDetection;
+using FluentAssertions;
+using Microsoft.Extensions.Configuration;
+using Xunit;
+
+namespace Application.Core.Tests.Validation;
+
+/// <summary>
+/// Tests for <see cref="DriftDetectionConfigValidator"/>.
+/// Pattern: CreateValidConfig() baseline, mutate one field per test.
+/// </summary>
+public class DriftDetectionConfigValidatorTests
+{
+    private readonly DriftDetectionConfigValidator _validator = new();
+
+    [Fact]
+    public void DefaultValues_MatchSpec()
+    {
+        var config = new DriftDetectionConfig();
+
+        config.Enabled.Should().BeTrue();
+        config.EwmaLambda.Should().Be(0.2);
+        config.ControlLimitWidth.Should().Be(3.0);
+        config.MinSamplesForBaseline.Should().Be(20);
+        config.BaselineWindowDays.Should().Be(7);
+        config.WarnThresholdSigma.Should().Be(1.5);
+        config.AlertThresholdSigma.Should().Be(2.5);
+        config.EscalateThresholdSigma.Should().Be(3.0);
+        config.EscalationEnabled.Should().BeTrue();
+        config.AuditPath.Should().Be("data/audit");
+    }
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
+    public async Task Validate_EwmaLambdaZero_HasError()
+    {
+        var config = CreateValidConfig();
+        config.EwmaLambda = 0;
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeFalse();
+        result.Errors.Should().Contain(e => e.PropertyName == "EwmaLambda");
+    }
+
+    [Fact]
+    public async Task Validate_EwmaLambdaNegative_HasError()
+    {
+        var config = CreateValidConfig();
+        config.EwmaLambda = -0.1;
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeFalse();
+        result.Errors.Should().Contain(e => e.PropertyName == "EwmaLambda");
+    }
+
+    [Fact]
+    public async Task Validate_EwmaLambdaGreaterThanOne_HasError()
+    {
+        var config = CreateValidConfig();
+        config.EwmaLambda = 1.1;
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeFalse();
+        result.Errors.Should().Contain(e => e.PropertyName == "EwmaLambda");
+    }
+
+    [Fact]
+    public async Task Validate_EwmaLambdaExactlyOne_NoError()
+    {
+        var config = CreateValidConfig();
+        config.EwmaLambda = 1.0;
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeTrue();
+    }
+
+    [Fact]
+    public async Task Validate_WarnThresholdGreaterThanOrEqualToAlert_HasError()
+    {
+        var config = CreateValidConfig();
+        config.WarnThresholdSigma = 2.5;
+        config.AlertThresholdSigma = 2.5;
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeFalse();
+        result.Errors.Should().Contain(e => e.PropertyName == "WarnThresholdSigma");
+    }
+
+    [Fact]
+    public async Task Validate_AlertThresholdGreaterThanOrEqualToEscalate_HasError()
+    {
+        var config = CreateValidConfig();
+        config.AlertThresholdSigma = 3.0;
+        config.EscalateThresholdSigma = 3.0;
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeFalse();
+        result.Errors.Should().Contain(e => e.PropertyName == "AlertThresholdSigma");
+    }
+
+    [Fact]
+    public async Task Validate_MinSamplesForBaselineZero_HasError()
+    {
+        var config = CreateValidConfig();
+        config.MinSamplesForBaseline = 0;
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeFalse();
+        result.Errors.Should().Contain(e => e.PropertyName == "MinSamplesForBaseline");
+    }
+
+    [Fact]
+    public async Task Validate_NegativeControlLimitWidth_HasError()
+    {
+        var config = CreateValidConfig();
+        config.ControlLimitWidth = -1.0;
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeFalse();
+        result.Errors.Should().Contain(e => e.PropertyName == "ControlLimitWidth");
+    }
+
+    [Fact]
+    public async Task Validate_BaselineWindowDaysZero_HasError()
+    {
+        var config = CreateValidConfig();
+        config.BaselineWindowDays = 0;
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeFalse();
+        result.Errors.Should().Contain(e => e.PropertyName == "BaselineWindowDays");
+    }
+
+    [Fact]
+    public async Task Validate_EmptyAuditPath_HasError()
+    {
+        var config = CreateValidConfig();
+        config.AuditPath = "";
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeFalse();
+        result.Errors.Should().Contain(e => e.PropertyName == "AuditPath");
+    }
+
+    [Fact]
+    public void BindsFromAppSettingsJson()
+    {
+        var inMemory = new Dictionary<string, string?>
+        {
+            ["AppConfig:AI:DriftDetection:Enabled"] = "false",
+            ["AppConfig:AI:DriftDetection:EwmaLambda"] = "0.3",
+            ["AppConfig:AI:DriftDetection:ControlLimitWidth"] = "2.5",
+            ["AppConfig:AI:DriftDetection:MinSamplesForBaseline"] = "30",
+            ["AppConfig:AI:DriftDetection:BaselineWindowDays"] = "14",
+            ["AppConfig:AI:DriftDetection:WarnThresholdSigma"] = "1.0",
+            ["AppConfig:AI:DriftDetection:AlertThresholdSigma"] = "2.0",
+            ["AppConfig:AI:DriftDetection:EscalateThresholdSigma"] = "2.8",
+            ["AppConfig:AI:DriftDetection:EscalationEnabled"] = "false",
+            ["AppConfig:AI:DriftDetection:AuditPath"] = "logs/drift-audit"
+        };
+
+        var configuration = new ConfigurationBuilder()
+            .AddInMemoryCollection(inMemory)
+            .Build();
+
+        var config = configuration
+            .GetSection("AppConfig:AI:DriftDetection")
+            .Get<DriftDetectionConfig>()!;
+
+        config.Enabled.Should().BeFalse();
+        config.EwmaLambda.Should().Be(0.3);
+        config.ControlLimitWidth.Should().Be(2.5);
+        config.MinSamplesForBaseline.Should().Be(30);
+        config.BaselineWindowDays.Should().Be(14);
+        config.WarnThresholdSigma.Should().Be(1.0);
+        config.AlertThresholdSigma.Should().Be(2.0);
+        config.EscalateThresholdSigma.Should().Be(2.8);
+        config.EscalationEnabled.Should().BeFalse();
+        config.AuditPath.Should().Be("logs/drift-audit");
+    }
+
+    private static DriftDetectionConfig CreateValidConfig() => new()
+    {
+        Enabled = true,
+        EwmaLambda = 0.2,
+        ControlLimitWidth = 3.0,
+        MinSamplesForBaseline = 20,
+        BaselineWindowDays = 7,
+        WarnThresholdSigma = 1.5,
+        AlertThresholdSigma = 2.5,
+        EscalateThresholdSigma = 3.0,
+        EscalationEnabled = true,
+        AuditPath = "data/audit"
+    };
+}
