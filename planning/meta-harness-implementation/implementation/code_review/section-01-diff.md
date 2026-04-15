diff --git a/src/Content/Domain/Domain.Common/Config/AppConfig.cs b/src/Content/Domain/Domain.Common/Config/AppConfig.cs
index 88a9e87..bbd7da7 100644
--- a/src/Content/Domain/Domain.Common/Config/AppConfig.cs
+++ b/src/Content/Domain/Domain.Common/Config/AppConfig.cs
@@ -4,6 +4,7 @@ using Domain.Common.Config.Cache;
 using Domain.Common.Config.Connectors;
 using Domain.Common.Config.Http;
 using Domain.Common.Config.Infrastructure;
+using Domain.Common.Config.MetaHarness;
 using Domain.Common.Config.Observability;
 
 namespace Domain.Common.Config;
@@ -30,7 +31,8 @@ namespace Domain.Common.Config;
 /// ├── Observability  — Sampling, PII filtering, rate limiting, exporters
 /// ├── AI             — MCP server/client, agent framework, model selection
 /// ├── Azure          — Azure platform services (AppInsights, SQL, B2C, KeyVault)
-/// └── Cache          — Caching strategy and Redis configuration
+/// ├── Cache          — Caching strategy and Redis configuration
+/// └── MetaHarness    — Automated harness optimization loop
 /// </code>
 /// </para>
 /// <para>
@@ -116,6 +118,11 @@ public class AppConfig
     /// Gets or sets the caching strategy and backing store configuration.
     /// </summary>
     public CacheConfig Cache { get; set; } = new();
+
+    /// <summary>
+    /// Gets or sets the meta-harness optimization loop configuration.
+    /// </summary>
+    public MetaHarnessConfig MetaHarness { get; set; } = new();
 }
 
 /// <summary>
diff --git a/src/Content/Domain/Domain.Common/Config/MetaHarness/MetaHarnessConfig.cs b/src/Content/Domain/Domain.Common/Config/MetaHarness/MetaHarnessConfig.cs
new file mode 100644
index 0000000..df6d1a5
--- /dev/null
+++ b/src/Content/Domain/Domain.Common/Config/MetaHarness/MetaHarnessConfig.cs
@@ -0,0 +1,141 @@
+namespace Domain.Common.Config.MetaHarness;
+
+/// <summary>
+/// Configuration for the meta-harness optimization loop.
+/// Binds to <c>AppConfig.MetaHarness</c> in appsettings.json.
+/// </summary>
+/// <remarks>
+/// <para>
+/// Controls iteration count, evaluation tasks, trace output, and proposer behavior.
+/// Each property has an inline default suitable for local development; override in
+/// appsettings.json for production or CI environments.
+/// </para>
+/// <para>
+/// <strong>Mutable setters are required by <c>IOptionsMonitor&lt;T&gt;</c> binding.</strong>
+/// Treat instances as read-only after DI setup. Do not mutate at runtime.
+/// </para>
+/// </remarks>
+// Mutable setters required by IOptionsMonitor<T> binding. Treat as read-only after DI setup.
+public class MetaHarnessConfig
+{
+    /// <summary>
+    /// Gets or sets the root path for all trace output directories.
+    /// Each optimization run and candidate evaluation writes trace files beneath this path.
+    /// </summary>
+    /// <value>Default: <c>"traces"</c>.</value>
+    public string TraceDirectoryRoot { get; set; } = "traces";
+
+    /// <summary>
+    /// Gets or sets the maximum number of propose-evaluate iterations per optimization run.
+    /// The loop exits early if no improvement is found within this limit.
+    /// </summary>
+    /// <value>Default: <c>10</c>.</value>
+    public int MaxIterations { get; set; } = 10;
+
+    /// <summary>
+    /// Gets or sets the maximum number of eval tasks sampled per candidate evaluation.
+    /// A random subset of size <c>SearchSetSize</c> is drawn from the full task pool.
+    /// </summary>
+    /// <value>Default: <c>50</c>.</value>
+    public int SearchSetSize { get; set; } = 50;
+
+    /// <summary>
+    /// Gets or sets the minimum pass-rate delta required for a candidate to be considered
+    /// an improvement over the current best. Candidates that improve by less than this
+    /// threshold are treated as ties and subject to cost tie-breaking.
+    /// </summary>
+    /// <value>Default: <c>0.01</c> (1% improvement).</value>
+    public double ScoreImprovementThreshold { get; set; } = 0.01;
+
+    /// <summary>
+    /// Gets or sets whether the best candidate is automatically applied to the live harness
+    /// after each optimization run. When <c>false</c>, the proposed changes are written to
+    /// the <c>_proposed/</c> output directory only.
+    /// </summary>
+    /// <value>Default: <c>false</c>.</value>
+    public bool AutoPromoteOnImprovement { get; set; } = false;
+
+    /// <summary>
+    /// Gets or sets the path to the directory containing evaluation task JSON files.
+    /// Each <c>.json</c> file in this directory is loaded as an <c>EvalTask</c> record.
+    /// </summary>
+    /// <value>Default: <c>"eval-tasks"</c>.</value>
+    public string EvalTasksPath { get; set; } = "eval-tasks";
+
+    /// <summary>
+    /// Gets or sets the optional path to a seed harness snapshot used as the first candidate.
+    /// When empty, the optimization loop seeds from the currently active harness configuration.
+    /// </summary>
+    /// <value>Default: <c>""</c> (use active configuration).</value>
+    public string SeedCandidatePath { get; set; } = "";
+
+    /// <summary>
+    /// Gets or sets the maximum number of eval tasks that may run in parallel.
+    /// Set to <c>1</c> for sequential execution, which is the default to avoid
+    /// overwhelming shared AI model rate limits during evaluation.
+    /// </summary>
+    /// <value>Default: <c>1</c>.</value>
+    public int MaxEvalParallelism { get; set; } = 1;
+
+    /// <summary>
+    /// Gets or sets the LLM sampling temperature used during evaluation runs.
+    /// A value of <c>0.0</c> produces deterministic, reproducible eval results.
+    /// </summary>
+    /// <value>Default: <c>0.0</c>.</value>
+    public double EvaluationTemperature { get; set; } = 0.0;
+
+    /// <summary>
+    /// Gets or sets an optional model deployment override for evaluation runs.
+    /// When <c>null</c>, the evaluation agent uses the default model from
+    /// <c>AppConfig.AI.AgentFramework.DefaultDeployment</c>.
+    /// </summary>
+    /// <value>Default: <c>null</c> (use default deployment).</value>
+    public string? EvaluationModelVersion { get; set; }
+
+    /// <summary>
+    /// Gets or sets the list of <c>AppConfig</c> key paths to include when taking
+    /// a harness configuration snapshot. Only keys matching these paths are captured;
+    /// secret keys are always excluded regardless of this list.
+    /// </summary>
+    /// <value>Default: empty (no config keys snapshotted).</value>
+    public IReadOnlyList<string> SnapshotConfigKeys { get; set; } = [];
+
+    /// <summary>
+    /// Gets or sets the list of config key substrings that are never included in harness
+    /// snapshots, even when matched by <see cref="SnapshotConfigKeys"/>. Protects API keys,
+    /// passwords, connection strings, and other sensitive values from being persisted to disk.
+    /// </summary>
+    /// <value>Default: <c>["Key", "Secret", "Token", "Password", "ConnectionString"]</c>.</value>
+    public IReadOnlyList<string> SecretsRedactionPatterns { get; set; } =
+        ["Key", "Secret", "Token", "Password", "ConnectionString"];
+
+    /// <summary>
+    /// Gets or sets the maximum size in kilobytes for per-call full payload artifacts.
+    /// Payloads exceeding this limit are truncated before being written to the trace directory.
+    /// </summary>
+    /// <value>Default: <c>512</c> KB.</value>
+    public int MaxFullPayloadKB { get; set; } = 512;
+
+    /// <summary>
+    /// Gets or sets the maximum number of optimization run directories to retain on disk.
+    /// Older runs beyond this limit are deleted to manage storage.
+    /// Set to <c>0</c> for unlimited retention.
+    /// </summary>
+    /// <value>Default: <c>20</c>.</value>
+    public int MaxRunsToKeep { get; set; } = 20;
+
+    /// <summary>
+    /// Gets or sets whether the proposer agent is permitted to execute restricted shell commands
+    /// via the <c>RestrictedSearchTool</c>. Disabled by default as an opt-in security boundary.
+    /// Enable only in controlled environments where the proposer is trusted.
+    /// </summary>
+    /// <value>Default: <c>false</c>.</value>
+    public bool EnableShellTool { get; set; } = false;
+
+    /// <summary>
+    /// Gets or sets whether completed trace runs are exposed as MCP resources at the
+    /// <c>trace://</c> URI scheme, allowing MCP clients to browse execution artifacts.
+    /// </summary>
+    /// <value>Default: <c>true</c>.</value>
+    public bool EnableMcpTraceResources { get; set; } = true;
+}
diff --git a/src/Content/Presentation/Presentation.ConsoleUI/appsettings.json b/src/Content/Presentation/Presentation.ConsoleUI/appsettings.json
index e230ab2..cdbbd8f 100644
--- a/src/Content/Presentation/Presentation.ConsoleUI/appsettings.json
+++ b/src/Content/Presentation/Presentation.ConsoleUI/appsettings.json
@@ -43,6 +43,15 @@
     },
     "Cache": {
       "CacheType": "Memory"
+    },
+    "MetaHarness": {
+      "TraceDirectoryRoot": "traces",
+      "MaxIterations": 10,
+      "EvalTasksPath": "eval-tasks",
+      "MaxEvalParallelism": 1,
+      "MaxRunsToKeep": 20,
+      "EnableShellTool": false,
+      "EnableMcpTraceResources": true
     }
   }
 }
diff --git a/src/Content/Tests/Application.AI.Common.Tests/Config/MetaHarnessConfigTests.cs b/src/Content/Tests/Application.AI.Common.Tests/Config/MetaHarnessConfigTests.cs
new file mode 100644
index 0000000..8a16c97
--- /dev/null
+++ b/src/Content/Tests/Application.AI.Common.Tests/Config/MetaHarnessConfigTests.cs
@@ -0,0 +1,73 @@
+using Domain.Common.Config.MetaHarness;
+using FluentAssertions;
+using Xunit;
+
+namespace Application.AI.Common.Tests.Config;
+
+public class MetaHarnessConfigTests
+{
+    /// <summary>Binds all properties from IOptions with an empty config source — verifies defaults.</summary>
+    [Fact]
+    public void MetaHarnessConfig_DefaultBinding_PopulatesAllDefaults()
+    {
+        var config = new MetaHarnessConfig();
+
+        config.TraceDirectoryRoot.Should().Be("traces");
+        config.MaxIterations.Should().Be(10);
+        config.SearchSetSize.Should().Be(50);
+        config.ScoreImprovementThreshold.Should().BeApproximately(0.01, 1e-10);
+        config.AutoPromoteOnImprovement.Should().BeFalse();
+        config.EvalTasksPath.Should().Be("eval-tasks");
+        config.SeedCandidatePath.Should().Be("");
+        config.MaxEvalParallelism.Should().Be(1);
+        config.EvaluationTemperature.Should().BeApproximately(0.0, 1e-10);
+        config.EvaluationModelVersion.Should().BeNull();
+        config.SnapshotConfigKeys.Should().BeEmpty();
+        config.SecretsRedactionPatterns.Should().HaveCount(5);
+        config.MaxFullPayloadKB.Should().Be(512);
+        config.MaxRunsToKeep.Should().Be(20);
+        config.EnableShellTool.Should().BeFalse();
+        config.EnableMcpTraceResources.Should().BeTrue();
+    }
+
+    /// <summary>TraceDirectoryRoot defaults to "traces" when not present in config.</summary>
+    [Fact]
+    public void TraceDirectoryRoot_NotConfigured_DefaultsToTraces()
+    {
+        var config = new MetaHarnessConfig();
+        config.TraceDirectoryRoot.Should().Be("traces");
+    }
+
+    /// <summary>MaxIterations defaults to 10.</summary>
+    [Fact]
+    public void MaxIterations_NotConfigured_DefaultsToTen()
+    {
+        var config = new MetaHarnessConfig();
+        config.MaxIterations.Should().Be(10);
+    }
+
+    /// <summary>SecretsRedactionPatterns contains Key, Secret, Token, Password, ConnectionString.</summary>
+    [Fact]
+    public void SecretsRedactionPatterns_NotConfigured_ContainsExpectedDefaults()
+    {
+        var config = new MetaHarnessConfig();
+        config.SecretsRedactionPatterns.Should().ContainInOrder(
+            "Key", "Secret", "Token", "Password", "ConnectionString");
+    }
+
+    /// <summary>EnableShellTool defaults to false.</summary>
+    [Fact]
+    public void EnableShellTool_NotConfigured_DefaultsToFalse()
+    {
+        var config = new MetaHarnessConfig();
+        config.EnableShellTool.Should().BeFalse();
+    }
+
+    /// <summary>MaxEvalParallelism defaults to 1.</summary>
+    [Fact]
+    public void MaxEvalParallelism_NotConfigured_DefaultsToOne()
+    {
+        var config = new MetaHarnessConfig();
+        config.MaxEvalParallelism.Should().Be(1);
+    }
+}
