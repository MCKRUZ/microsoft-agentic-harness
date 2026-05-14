diff --git a/src/Content/Application/Application.Core/Validation/LearningsConfigValidator.cs b/src/Content/Application/Application.Core/Validation/LearningsConfigValidator.cs
new file mode 100644
index 0000000..911f976
--- /dev/null
+++ b/src/Content/Application/Application.Core/Validation/LearningsConfigValidator.cs
@@ -0,0 +1,42 @@
+using Domain.Common.Config.AI.Learnings;
+using FluentValidation;
+
+namespace Application.Core.Validation;
+
+/// <summary>
+/// Validates <see cref="LearningsConfig"/> constraints including feedback blending ranges,
+/// diversity ratio bounds, shelf life positivity, and baseline adjustment threshold.
+/// </summary>
+public sealed class LearningsConfigValidator : AbstractValidator<LearningsConfig>
+{
+    public LearningsConfigValidator()
+    {
+        RuleFor(x => x.FeedbackAlpha)
+            .GreaterThan(0).WithMessage("FeedbackAlpha must be > 0.")
+            .LessThanOrEqualTo(1).WithMessage("FeedbackAlpha must be <= 1.");
+
+        RuleFor(x => x.FeedbackCeiling)
+            .GreaterThan(0).WithMessage("FeedbackCeiling must be > 0.")
+            .LessThanOrEqualTo(1).WithMessage("FeedbackCeiling must be <= 1.");
+
+        RuleFor(x => x.DiversityInjectionRatio)
+            .GreaterThanOrEqualTo(0).WithMessage("DiversityInjectionRatio must be >= 0.")
+            .LessThanOrEqualTo(0.5).WithMessage("DiversityInjectionRatio must be <= 0.5.");
+
+        RuleFor(x => x.VolatileShelfLifeDays)
+            .GreaterThan(0).WithMessage("VolatileShelfLifeDays must be > 0.");
+
+        RuleFor(x => x.StableShelfLifeDays)
+            .GreaterThan(0).WithMessage("StableShelfLifeDays must be > 0.");
+
+        RuleFor(x => x.PruneIntervalHours)
+            .GreaterThan(0).WithMessage("PruneIntervalHours must be > 0.");
+
+        RuleFor(x => x.BaselineAdjustmentThreshold)
+            .GreaterThan(0).WithMessage("BaselineAdjustmentThreshold must be > 0.")
+            .LessThanOrEqualTo(1).WithMessage("BaselineAdjustmentThreshold must be <= 1.");
+
+        RuleFor(x => x.StoreProvider)
+            .NotEmpty().WithMessage("StoreProvider must be configured (e.g., 'graph' or 'in_memory').");
+    }
+}
diff --git a/src/Content/Domain/Domain.Common/Config/AI/AIConfig.cs b/src/Content/Domain/Domain.Common/Config/AI/AIConfig.cs
index 29fccd2..07719f7 100644
--- a/src/Content/Domain/Domain.Common/Config/AI/AIConfig.cs
+++ b/src/Content/Domain/Domain.Common/Config/AI/AIConfig.cs
@@ -3,6 +3,7 @@ using Domain.Common.Config.AI.AIFoundry;
 using Domain.Common.Config.AI.ContextManagement;
 using Domain.Common.Config.AI.DriftDetection;
 using Domain.Common.Config.AI.Hooks;
+using Domain.Common.Config.AI.Learnings;
 using Domain.Common.Config.AI.MCP;
 using Domain.Common.Config.AI.Orchestration;
 using Domain.Common.Config.AI.Permissions;
@@ -31,7 +32,8 @@ namespace Domain.Common.Config.AI;
 /// ├── Orchestration     — Subagent management and streaming execution
 /// ├── Resilience        — LLM fallback chains, circuit breakers, retry, degraded mode
 /// ├── Rag               — RAG pipeline: ingestion, retrieval, reranking, model tiering
-/// └── DriftDetection    — EWMA-based drift detection for quality regressions
+/// ├── DriftDetection    — EWMA-based drift detection for quality regressions
+/// └── Learnings         — Cross-session learnings: feedback blending, decay, pruning
 /// </code>
 /// </para>
 /// </remarks>
diff --git a/src/Content/Domain/Domain.Common/Config/AI/Learnings/LearningsConfig.cs b/src/Content/Domain/Domain.Common/Config/AI/Learnings/LearningsConfig.cs
new file mode 100644
index 0000000..f1f4a6c
--- /dev/null
+++ b/src/Content/Domain/Domain.Common/Config/AI/Learnings/LearningsConfig.cs
@@ -0,0 +1,94 @@
+namespace Domain.Common.Config.AI.Learnings;
+
+/// <summary>
+/// Root configuration for the cross-session learnings subsystem.
+/// Bound from <c>AppConfig:AI:Learnings</c> in appsettings.json.
+/// </summary>
+/// <remarks>
+/// Configuration hierarchy:
+/// <code>
+/// AppConfig.AI.Learnings
+/// +-- Enabled                     -- Master toggle for learnings subsystem
+/// +-- StoreProvider               -- Keyed DI provider ("graph" or "in_memory")
+/// +-- FeedbackAlpha               -- EMA blending weight for feedback in recall
+/// +-- FeedbackCeiling             -- Maximum feedback influence on recall score
+/// +-- DiversityInjectionRatio     -- Fraction of results replaced by random learnings
+/// +-- VolatileShelfLifeDays       -- Decay window for Volatile learnings
+/// +-- StableShelfLifeDays         -- Decay window for Stable learnings
+/// +-- PruneIntervalHours          -- Background pruning service interval
+/// +-- BaselineAdjustmentThreshold -- Min FeedbackWeight for drift baseline adjustment
+/// +-- BiasCorrection              -- Bias-corrected EMA for new learnings
+/// </code>
+/// </remarks>
+public class LearningsConfig
+{
+    /// <summary>
+    /// Master toggle. When disabled, all learnings operations return success no-ops.
+    /// </summary>
+    public bool Enabled { get; set; } = true;
+
+    /// <summary>
+    /// Keyed DI provider for <c>ILearningsStore</c> ("graph" or "in_memory").
+    /// </summary>
+    /// <value>Default: "graph"</value>
+    public string StoreProvider { get; set; } = "graph";
+
+    /// <summary>
+    /// EMA blending weight for feedback in recall scoring formula.
+    /// Higher values weight feedback more heavily relative to semantic similarity.
+    /// Must be in range (0, 1].
+    /// </summary>
+    /// <value>Default: 0.25</value>
+    public double FeedbackAlpha { get; set; } = 0.25;
+
+    /// <summary>
+    /// Maximum influence feedback can exert on final recall score.
+    /// Prevents feedback from dominating semantic relevance.
+    /// Must be in range (0, 1].
+    /// </summary>
+    /// <value>Default: 0.3</value>
+    public double FeedbackCeiling { get; set; } = 0.3;
+
+    /// <summary>
+    /// Fraction of recall results replaced by random non-feedback-optimized learnings.
+    /// Prevents filter bubbles. Zero disables diversity injection.
+    /// Must be in range [0, 0.5].
+    /// </summary>
+    /// <value>Default: 0.15</value>
+    public double DiversityInjectionRatio { get; set; } = 0.15;
+
+    /// <summary>
+    /// Shelf life in days for <c>DecayClass.Volatile</c> learnings.
+    /// After this window, freshness decays to zero.
+    /// </summary>
+    /// <value>Default: 7</value>
+    public int VolatileShelfLifeDays { get; set; } = 7;
+
+    /// <summary>
+    /// Shelf life in days for <c>DecayClass.Stable</c> learnings.
+    /// After this window, freshness decays to zero.
+    /// </summary>
+    /// <value>Default: 180</value>
+    public int StableShelfLifeDays { get; set; } = 180;
+
+    /// <summary>
+    /// Interval in hours for the <c>LearningsPruningBackgroundService</c>.
+    /// </summary>
+    /// <value>Default: 24</value>
+    public int PruneIntervalHours { get; set; } = 24;
+
+    /// <summary>
+    /// Minimum <c>FeedbackWeight</c> before a learning can trigger drift baseline
+    /// recalculation via the learnings-drift bridge.
+    /// Must be in range (0, 1].
+    /// </summary>
+    /// <value>Default: 0.8</value>
+    public double BaselineAdjustmentThreshold { get; set; } = 0.8;
+
+    /// <summary>
+    /// Whether to apply bias-corrected EMA for new learnings with fewer than 5 updates.
+    /// Prevents early observations from dominating the feedback weight.
+    /// </summary>
+    /// <value>Default: true</value>
+    public bool BiasCorrection { get; set; } = true;
+}
diff --git a/src/Content/Tests/Application.Core.Tests/Validation/LearningsConfigValidatorTests.cs b/src/Content/Tests/Application.Core.Tests/Validation/LearningsConfigValidatorTests.cs
new file mode 100644
index 0000000..fb2b053
--- /dev/null
+++ b/src/Content/Tests/Application.Core.Tests/Validation/LearningsConfigValidatorTests.cs
@@ -0,0 +1,308 @@
+using Application.Core.Validation;
+using Domain.Common.Config.AI.Learnings;
+using FluentAssertions;
+using Microsoft.Extensions.Configuration;
+using Xunit;
+
+namespace Application.Core.Tests.Validation;
+
+/// <summary>
+/// Tests for <see cref="LearningsConfigValidator"/>.
+/// Pattern: CreateValidConfig() baseline, mutate one field per test.
+/// </summary>
+public class LearningsConfigValidatorTests
+{
+    private readonly LearningsConfigValidator _validator = new();
+
+    [Fact]
+    public void DefaultValues_MatchSpec()
+    {
+        var config = new LearningsConfig();
+
+        config.Enabled.Should().BeTrue();
+        config.StoreProvider.Should().Be("graph");
+        config.FeedbackAlpha.Should().Be(0.25);
+        config.FeedbackCeiling.Should().Be(0.3);
+        config.DiversityInjectionRatio.Should().Be(0.15);
+        config.VolatileShelfLifeDays.Should().Be(7);
+        config.StableShelfLifeDays.Should().Be(180);
+        config.PruneIntervalHours.Should().Be(24);
+        config.BaselineAdjustmentThreshold.Should().Be(0.8);
+        config.BiasCorrection.Should().BeTrue();
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
+    public async Task Validate_FeedbackAlphaZero_HasError()
+    {
+        var config = CreateValidConfig();
+        config.FeedbackAlpha = 0;
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeFalse();
+        result.Errors.Should().Contain(e => e.PropertyName == "FeedbackAlpha");
+    }
+
+    [Fact]
+    public async Task Validate_FeedbackAlphaNegative_HasError()
+    {
+        var config = CreateValidConfig();
+        config.FeedbackAlpha = -0.1;
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeFalse();
+        result.Errors.Should().Contain(e => e.PropertyName == "FeedbackAlpha");
+    }
+
+    [Fact]
+    public async Task Validate_FeedbackAlphaAboveOne_HasError()
+    {
+        var config = CreateValidConfig();
+        config.FeedbackAlpha = 1.1;
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeFalse();
+        result.Errors.Should().Contain(e => e.PropertyName == "FeedbackAlpha");
+    }
+
+    [Fact]
+    public async Task Validate_FeedbackAlphaExactlyOne_Allowed()
+    {
+        var config = CreateValidConfig();
+        config.FeedbackAlpha = 1.0;
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeTrue();
+    }
+
+    [Fact]
+    public async Task Validate_FeedbackCeilingZero_HasError()
+    {
+        var config = CreateValidConfig();
+        config.FeedbackCeiling = 0;
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeFalse();
+        result.Errors.Should().Contain(e => e.PropertyName == "FeedbackCeiling");
+    }
+
+    [Fact]
+    public async Task Validate_FeedbackCeilingNegative_HasError()
+    {
+        var config = CreateValidConfig();
+        config.FeedbackCeiling = -0.5;
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeFalse();
+        result.Errors.Should().Contain(e => e.PropertyName == "FeedbackCeiling");
+    }
+
+    [Fact]
+    public async Task Validate_FeedbackCeilingAboveOne_HasError()
+    {
+        var config = CreateValidConfig();
+        config.FeedbackCeiling = 1.5;
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeFalse();
+        result.Errors.Should().Contain(e => e.PropertyName == "FeedbackCeiling");
+    }
+
+    [Fact]
+    public async Task Validate_DiversityRatioNegative_HasError()
+    {
+        var config = CreateValidConfig();
+        config.DiversityInjectionRatio = -0.1;
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeFalse();
+        result.Errors.Should().Contain(e => e.PropertyName == "DiversityInjectionRatio");
+    }
+
+    [Fact]
+    public async Task Validate_DiversityRatioAboveHalf_HasError()
+    {
+        var config = CreateValidConfig();
+        config.DiversityInjectionRatio = 0.6;
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeFalse();
+        result.Errors.Should().Contain(e => e.PropertyName == "DiversityInjectionRatio");
+    }
+
+    [Fact]
+    public async Task Validate_DiversityRatioZero_Allowed()
+    {
+        var config = CreateValidConfig();
+        config.DiversityInjectionRatio = 0;
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeTrue();
+    }
+
+    [Fact]
+    public async Task Validate_DiversityRatioExactlyHalf_Allowed()
+    {
+        var config = CreateValidConfig();
+        config.DiversityInjectionRatio = 0.5;
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeTrue();
+    }
+
+    [Fact]
+    public async Task Validate_VolatileShelfLifeZero_HasError()
+    {
+        var config = CreateValidConfig();
+        config.VolatileShelfLifeDays = 0;
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeFalse();
+        result.Errors.Should().Contain(e => e.PropertyName == "VolatileShelfLifeDays");
+    }
+
+    [Fact]
+    public async Task Validate_VolatileShelfLifeNegative_HasError()
+    {
+        var config = CreateValidConfig();
+        config.VolatileShelfLifeDays = -1;
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeFalse();
+        result.Errors.Should().Contain(e => e.PropertyName == "VolatileShelfLifeDays");
+    }
+
+    [Fact]
+    public async Task Validate_StableShelfLifeZero_HasError()
+    {
+        var config = CreateValidConfig();
+        config.StableShelfLifeDays = 0;
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeFalse();
+        result.Errors.Should().Contain(e => e.PropertyName == "StableShelfLifeDays");
+    }
+
+    [Fact]
+    public async Task Validate_PruneIntervalZero_HasError()
+    {
+        var config = CreateValidConfig();
+        config.PruneIntervalHours = 0;
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeFalse();
+        result.Errors.Should().Contain(e => e.PropertyName == "PruneIntervalHours");
+    }
+
+    [Fact]
+    public async Task Validate_BaselineAdjustmentThresholdZero_HasError()
+    {
+        var config = CreateValidConfig();
+        config.BaselineAdjustmentThreshold = 0;
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeFalse();
+        result.Errors.Should().Contain(e => e.PropertyName == "BaselineAdjustmentThreshold");
+    }
+
+    [Fact]
+    public async Task Validate_BaselineAdjustmentThresholdAboveOne_HasError()
+    {
+        var config = CreateValidConfig();
+        config.BaselineAdjustmentThreshold = 1.1;
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeFalse();
+        result.Errors.Should().Contain(e => e.PropertyName == "BaselineAdjustmentThreshold");
+    }
+
+    [Fact]
+    public async Task Validate_EmptyStoreProvider_HasError()
+    {
+        var config = CreateValidConfig();
+        config.StoreProvider = "";
+
+        var result = await _validator.ValidateAsync(config);
+
+        result.IsValid.Should().BeFalse();
+        result.Errors.Should().Contain(e => e.PropertyName == "StoreProvider");
+    }
+
+    [Fact]
+    public void BindsFromAppSettingsJson()
+    {
+        var inMemory = new Dictionary<string, string?>
+        {
+            ["AppConfig:AI:Learnings:Enabled"] = "false",
+            ["AppConfig:AI:Learnings:StoreProvider"] = "in_memory",
+            ["AppConfig:AI:Learnings:FeedbackAlpha"] = "0.5",
+            ["AppConfig:AI:Learnings:FeedbackCeiling"] = "0.4",
+            ["AppConfig:AI:Learnings:DiversityInjectionRatio"] = "0.2",
+            ["AppConfig:AI:Learnings:VolatileShelfLifeDays"] = "14",
+            ["AppConfig:AI:Learnings:StableShelfLifeDays"] = "365",
+            ["AppConfig:AI:Learnings:PruneIntervalHours"] = "12",
+            ["AppConfig:AI:Learnings:BaselineAdjustmentThreshold"] = "0.9",
+            ["AppConfig:AI:Learnings:BiasCorrection"] = "false"
+        };
+
+        var configuration = new ConfigurationBuilder()
+            .AddInMemoryCollection(inMemory)
+            .Build();
+
+        var config = configuration
+            .GetSection("AppConfig:AI:Learnings")
+            .Get<LearningsConfig>()!;
+
+        config.Enabled.Should().BeFalse();
+        config.StoreProvider.Should().Be("in_memory");
+        config.FeedbackAlpha.Should().Be(0.5);
+        config.FeedbackCeiling.Should().Be(0.4);
+        config.DiversityInjectionRatio.Should().Be(0.2);
+        config.VolatileShelfLifeDays.Should().Be(14);
+        config.StableShelfLifeDays.Should().Be(365);
+        config.PruneIntervalHours.Should().Be(12);
+        config.BaselineAdjustmentThreshold.Should().Be(0.9);
+        config.BiasCorrection.Should().BeFalse();
+    }
+
+    private static LearningsConfig CreateValidConfig() => new()
+    {
+        Enabled = true,
+        StoreProvider = "graph",
+        FeedbackAlpha = 0.25,
+        FeedbackCeiling = 0.3,
+        DiversityInjectionRatio = 0.15,
+        VolatileShelfLifeDays = 7,
+        StableShelfLifeDays = 180,
+        PruneIntervalHours = 24,
+        BaselineAdjustmentThreshold = 0.8,
+        BiasCorrection = true
+    };
+}
