diff --git a/src/Content/Application/Application.AI.Common/Interfaces/MetaHarness/ISnapshotBuilder.cs b/src/Content/Application/Application.AI.Common/Interfaces/MetaHarness/ISnapshotBuilder.cs
new file mode 100644
index 0000000..d4c6134
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/Interfaces/MetaHarness/ISnapshotBuilder.cs
@@ -0,0 +1,27 @@
+using Domain.Common.MetaHarness;
+
+namespace Application.AI.Common.Interfaces.MetaHarness;
+
+/// <summary>
+/// Builds a <see cref="HarnessSnapshot"/> from the currently active harness configuration.
+/// Secrets are excluded and SHA256 hashes computed for all skill files.
+/// </summary>
+public interface ISnapshotBuilder
+{
+    /// <summary>
+    /// Captures the active harness state into an immutable, redacted snapshot.
+    /// </summary>
+    /// <param name="skillDirectory">Absolute path to the agent's skill directory.</param>
+    /// <param name="systemPrompt">Current system prompt text (will be redacted).</param>
+    /// <param name="configValues">
+    /// Key/value pairs from AppConfig to snapshot. Only keys in
+    /// <see cref="Domain.Common.Config.MetaHarness.MetaHarnessConfig.SnapshotConfigKeys"/> and not matching any
+    /// <see cref="Domain.Common.Config.MetaHarness.MetaHarnessConfig.SecretsRedactionPatterns"/> will be included.
+    /// </param>
+    /// <param name="cancellationToken"/>
+    Task<HarnessSnapshot> BuildAsync(
+        string skillDirectory,
+        string systemPrompt,
+        IReadOnlyDictionary<string, string> configValues,
+        CancellationToken cancellationToken = default);
+}
diff --git a/src/Content/Domain/Domain.Common/MetaHarness/EvalTask.cs b/src/Content/Domain/Domain.Common/MetaHarness/EvalTask.cs
new file mode 100644
index 0000000..17606ec
--- /dev/null
+++ b/src/Content/Domain/Domain.Common/MetaHarness/EvalTask.cs
@@ -0,0 +1,26 @@
+namespace Domain.Common.MetaHarness;
+
+/// <summary>
+/// A single evaluation task used to score a <see cref="HarnessCandidate"/>.
+/// Loaded from JSON files under <c>MetaHarnessConfig.EvalTasksPath</c>.
+/// </summary>
+public sealed record EvalTask
+{
+    /// <summary>Stable unique identifier for this task (used in trace paths).</summary>
+    public required string TaskId { get; init; }
+
+    /// <summary>Human-readable description of what the task exercises.</summary>
+    public required string Description { get; init; }
+
+    /// <summary>The prompt sent to the agent under evaluation.</summary>
+    public required string InputPrompt { get; init; }
+
+    /// <summary>
+    /// Optional .NET regex pattern. Agent output must match for the task to pass.
+    /// Null means the task is always considered passed (useful for smoke tests).
+    /// </summary>
+    public string? ExpectedOutputPattern { get; init; }
+
+    /// <summary>Arbitrary tags for filtering (e.g., "smoke", "regression").</summary>
+    public IReadOnlyList<string> Tags { get; init; } = [];
+}
diff --git a/src/Content/Domain/Domain.Common/MetaHarness/HarnessCandidate.cs b/src/Content/Domain/Domain.Common/MetaHarness/HarnessCandidate.cs
new file mode 100644
index 0000000..fc60410
--- /dev/null
+++ b/src/Content/Domain/Domain.Common/MetaHarness/HarnessCandidate.cs
@@ -0,0 +1,43 @@
+namespace Domain.Common.MetaHarness;
+
+/// <summary>
+/// Immutable domain record representing one proposed harness configuration within an
+/// optimization run. Status transitions are performed via <c>with</c> expressions.
+/// </summary>
+public sealed record HarnessCandidate
+{
+    /// <summary>Stable unique identifier for this candidate.</summary>
+    public required Guid CandidateId { get; init; }
+
+    /// <summary>The optimization run this candidate belongs to.</summary>
+    public required Guid OptimizationRunId { get; init; }
+
+    /// <summary>
+    /// Null for the seed candidate; set to the parent's <see cref="CandidateId"/> for all proposals.
+    /// </summary>
+    public Guid? ParentCandidateId { get; init; }
+
+    /// <summary>Zero-based iteration index within the optimization run.</summary>
+    public required int Iteration { get; init; }
+
+    /// <summary>UTC timestamp when this candidate was created.</summary>
+    public required DateTimeOffset CreatedAt { get; init; }
+
+    /// <summary>The harness configuration snapshot this candidate represents.</summary>
+    public required HarnessSnapshot Snapshot { get; init; }
+
+    /// <summary>Pass rate [0.0, 1.0] after evaluation. Null until evaluated.</summary>
+    public double? BestScore { get; init; }
+
+    /// <summary>Cumulative LLM token cost across all eval task runs. Null until evaluated.</summary>
+    public long? TokenCost { get; init; }
+
+    /// <summary>Current lifecycle state of this candidate.</summary>
+    public required HarnessCandidateStatus Status { get; init; }
+
+    /// <summary>
+    /// Human-readable failure message. Only set when <see cref="Status"/> is
+    /// <see cref="HarnessCandidateStatus.Failed"/>.
+    /// </summary>
+    public string? FailureReason { get; init; }
+}
diff --git a/src/Content/Domain/Domain.Common/MetaHarness/HarnessCandidateStatus.cs b/src/Content/Domain/Domain.Common/MetaHarness/HarnessCandidateStatus.cs
new file mode 100644
index 0000000..0da4b0d
--- /dev/null
+++ b/src/Content/Domain/Domain.Common/MetaHarness/HarnessCandidateStatus.cs
@@ -0,0 +1,16 @@
+namespace Domain.Common.MetaHarness;
+
+/// <summary>
+/// Lifecycle states for a <see cref="HarnessCandidate"/> within an optimization run.
+/// </summary>
+public enum HarnessCandidateStatus
+{
+    /// <summary>The candidate has been proposed but not yet evaluated.</summary>
+    Proposed,
+    /// <summary>The candidate has been fully evaluated and scored.</summary>
+    Evaluated,
+    /// <summary>Evaluation or proposal failed; see <see cref="HarnessCandidate.FailureReason"/>.</summary>
+    Failed,
+    /// <summary>The candidate has been promoted to the active harness configuration.</summary>
+    Promoted
+}
diff --git a/src/Content/Domain/Domain.Common/MetaHarness/HarnessSnapshot.cs b/src/Content/Domain/Domain.Common/MetaHarness/HarnessSnapshot.cs
new file mode 100644
index 0000000..752d9c0
--- /dev/null
+++ b/src/Content/Domain/Domain.Common/MetaHarness/HarnessSnapshot.cs
@@ -0,0 +1,31 @@
+namespace Domain.Common.MetaHarness;
+
+/// <summary>
+/// Immutable, redacted snapshot of a harness configuration at a specific point in time.
+/// Used to reproduce and compare candidate harness configurations during optimization.
+/// </summary>
+public sealed record HarnessSnapshot
+{
+    /// <summary>
+    /// Skill file path → content for the active agent's skill directory only.
+    /// Secrets have been removed via the <c>ISecretRedactor</c> before capture.
+    /// </summary>
+    public required IReadOnlyDictionary<string, string> SkillFileSnapshots { get; init; }
+
+    /// <summary>
+    /// System prompt at snapshot time, with secrets redacted.
+    /// </summary>
+    public required string SystemPromptSnapshot { get; init; }
+
+    /// <summary>
+    /// Selected AppConfig key/value pairs as declared in
+    /// <c>MetaHarnessConfig.SnapshotConfigKeys</c>, minus any secret keys.
+    /// </summary>
+    public required IReadOnlyDictionary<string, string> ConfigSnapshot { get; init; }
+
+    /// <summary>
+    /// Per-file SHA256 hashes for all entries in <see cref="SkillFileSnapshots"/>.
+    /// Enables verification that a snapshot can be faithfully reconstructed.
+    /// </summary>
+    public required IReadOnlyList<SnapshotEntry> SnapshotManifest { get; init; }
+}
diff --git a/src/Content/Domain/Domain.Common/MetaHarness/SnapshotEntry.cs b/src/Content/Domain/Domain.Common/MetaHarness/SnapshotEntry.cs
new file mode 100644
index 0000000..9c4eeba
--- /dev/null
+++ b/src/Content/Domain/Domain.Common/MetaHarness/SnapshotEntry.cs
@@ -0,0 +1,11 @@
+namespace Domain.Common.MetaHarness;
+
+/// <summary>
+/// An entry in a <see cref="HarnessSnapshot.SnapshotManifest"/> recording the SHA256
+/// hash of a single skill file for reproducibility verification.
+/// </summary>
+public sealed record SnapshotEntry(
+    /// <summary>Relative skill file path (e.g., "skills/research-agent/SKILL.md").</summary>
+    string Path,
+    /// <summary>Lowercase hex SHA256 hash of the file contents at snapshot time.</summary>
+    string Sha256Hash);
diff --git a/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs b/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs
index dc656db..51c17c2 100644
--- a/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs
+++ b/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs
@@ -1,5 +1,6 @@
 using Application.AI.Common.Interfaces;
 using Application.AI.Common.Interfaces.A2A;
+using Application.AI.Common.Interfaces.MetaHarness;
 using Application.AI.Common.Interfaces.Skills;
 using Application.AI.Common.Interfaces.Memory;
 using Application.AI.Common.Interfaces.Traces;
@@ -20,6 +21,7 @@ using Azure.AI.OpenAI;
 using Domain.Common.Config;
 using Domain.Common.Config.AI;
 using Infrastructure.AI.A2A;
+using Infrastructure.AI.MetaHarness;
 using Infrastructure.AI.Audit;
 using Infrastructure.AI.ContentSafety;
 using OpenAI;
@@ -74,6 +76,9 @@ public static class DependencyInjection
         // Secret redaction — applied at all persistence boundaries (traces, snapshots, manifests)
         services.AddSingleton<ISecretRedactor, PatternSecretRedactor>();
 
+        // Snapshot builder — captures live harness config into a redacted, hashed snapshot
+        services.AddSingleton<ISnapshotBuilder, ActiveConfigSnapshotBuilder>();
+
         // Execution trace store — filesystem-backed per-run trace artifact persistence
         services.AddSingleton<IExecutionTraceStore, FileSystemExecutionTraceStore>();
 
diff --git a/src/Content/Infrastructure/Infrastructure.AI/MetaHarness/ActiveConfigSnapshotBuilder.cs b/src/Content/Infrastructure/Infrastructure.AI/MetaHarness/ActiveConfigSnapshotBuilder.cs
new file mode 100644
index 0000000..ed896f1
--- /dev/null
+++ b/src/Content/Infrastructure/Infrastructure.AI/MetaHarness/ActiveConfigSnapshotBuilder.cs
@@ -0,0 +1,104 @@
+using Application.AI.Common.Interfaces;
+using Application.AI.Common.Interfaces.MetaHarness;
+using Domain.Common.Config.MetaHarness;
+using Domain.Common.MetaHarness;
+using Microsoft.Extensions.Options;
+using System.Security.Cryptography;
+using System.Text;
+
+namespace Infrastructure.AI.MetaHarness;
+
+/// <summary>
+/// Builds a <see cref="HarnessSnapshot"/> from the live filesystem and configuration.
+/// Applies <see cref="ISecretRedactor"/> to all content before capture.
+/// </summary>
+public sealed class ActiveConfigSnapshotBuilder : ISnapshotBuilder
+{
+    private readonly MetaHarnessConfig _config;
+    private readonly ISecretRedactor _redactor;
+
+    /// <summary>
+    /// Initializes a new instance of <see cref="ActiveConfigSnapshotBuilder"/>.
+    /// </summary>
+    public ActiveConfigSnapshotBuilder(
+        IOptionsMonitor<MetaHarnessConfig> options,
+        ISecretRedactor redactor)
+    {
+        _config = options.CurrentValue;
+        _redactor = redactor;
+    }
+
+    /// <inheritdoc/>
+    public async Task<HarnessSnapshot> BuildAsync(
+        string skillDirectory,
+        string systemPrompt,
+        IReadOnlyDictionary<string, string> configValues,
+        CancellationToken cancellationToken = default)
+    {
+        var skillFiles = await EnumerateSkillFilesAsync(skillDirectory, cancellationToken);
+
+        var configSnapshot = BuildConfigSnapshot(configValues);
+        var redactedPrompt = _redactor.Redact(systemPrompt) ?? string.Empty;
+
+        return new HarnessSnapshot
+        {
+            SkillFileSnapshots = skillFiles.ToDictionary(
+                kvp => kvp.Key,
+                kvp => _redactor.Redact(kvp.Value) ?? string.Empty),
+            SystemPromptSnapshot = redactedPrompt,
+            ConfigSnapshot = configSnapshot,
+            SnapshotManifest = skillFiles
+                .Select(kvp => new SnapshotEntry(
+                    kvp.Key,
+                    ComputeSha256Hex(Encoding.UTF8.GetBytes(kvp.Value))))
+                .ToList()
+        };
+    }
+
+    private static async Task<Dictionary<string, string>> EnumerateSkillFilesAsync(
+        string skillDirectory,
+        CancellationToken cancellationToken)
+    {
+        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
+
+        if (!Directory.Exists(skillDirectory))
+            return result;
+
+        foreach (var filePath in Directory.EnumerateFiles(skillDirectory, "*", SearchOption.AllDirectories))
+        {
+            cancellationToken.ThrowIfCancellationRequested();
+            var relativePath = Path.GetRelativePath(skillDirectory, filePath)
+                .Replace('\\', '/');
+            result[relativePath] = await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken);
+        }
+
+        return result;
+    }
+
+    private IReadOnlyDictionary<string, string> BuildConfigSnapshot(
+        IReadOnlyDictionary<string, string> configValues)
+    {
+        var result = new Dictionary<string, string>();
+
+        foreach (var key in _config.SnapshotConfigKeys)
+        {
+            if (IsSecretKey(key))
+                continue;
+
+            if (configValues.TryGetValue(key, out var value))
+                result[key] = value;
+        }
+
+        return result;
+    }
+
+    private static string ComputeSha256Hex(byte[] bytes)
+    {
+        var hash = SHA256.HashData(bytes);
+        return Convert.ToHexString(hash).ToLowerInvariant();
+    }
+
+    private bool IsSecretKey(string key) =>
+        _config.SecretsRedactionPatterns.Any(p =>
+            key.Contains(p, StringComparison.OrdinalIgnoreCase));
+}
diff --git a/src/Content/Tests/Application.AI.Common.Tests/MetaHarness/HarnessCandidateTests.cs b/src/Content/Tests/Application.AI.Common.Tests/MetaHarness/HarnessCandidateTests.cs
new file mode 100644
index 0000000..ac70c7c
--- /dev/null
+++ b/src/Content/Tests/Application.AI.Common.Tests/MetaHarness/HarnessCandidateTests.cs
@@ -0,0 +1,76 @@
+using Domain.Common.MetaHarness;
+using Xunit;
+
+namespace Application.AI.Common.Tests.MetaHarness;
+
+/// <summary>
+/// Tests for HarnessCandidate domain model immutability and HarnessSnapshot integrity.
+/// </summary>
+public class HarnessCandidateTests
+{
+    private static HarnessSnapshot BuildSnapshot() => new()
+    {
+        SkillFileSnapshots = new Dictionary<string, string>
+        {
+            ["skills/agent/SKILL.md"] = "# Skill",
+            ["skills/agent/TOOL.md"] = "# Tool"
+        },
+        SystemPromptSnapshot = "You are a helpful assistant.",
+        ConfigSnapshot = new Dictionary<string, string> { ["Region"] = "eastus" },
+        SnapshotManifest =
+        [
+            new SnapshotEntry("skills/agent/SKILL.md", "abc123"),
+            new SnapshotEntry("skills/agent/TOOL.md", "def456")
+        ]
+    };
+
+    private static HarnessCandidate BuildCandidate(HarnessCandidateStatus status = HarnessCandidateStatus.Proposed) =>
+        new()
+        {
+            CandidateId = Guid.NewGuid(),
+            OptimizationRunId = Guid.NewGuid(),
+            Iteration = 0,
+            CreatedAt = DateTimeOffset.UtcNow,
+            Snapshot = BuildSnapshot(),
+            Status = status
+        };
+
+    [Fact]
+    public void HarnessCandidate_StatusTransition_ProducesNewImmutableRecord()
+    {
+        var original = BuildCandidate(HarnessCandidateStatus.Proposed);
+
+        var updated = original with { Status = HarnessCandidateStatus.Evaluated };
+
+        Assert.Equal(HarnessCandidateStatus.Proposed, original.Status);
+        Assert.Equal(HarnessCandidateStatus.Evaluated, updated.Status);
+        Assert.False(ReferenceEquals(original, updated));
+    }
+
+    [Fact]
+    public void HarnessCandidate_WithExpression_DoesNotMutateOriginal()
+    {
+        var candidate = BuildCandidate();
+        var originalScore = candidate.BestScore;
+
+        var updated = candidate with { BestScore = 0.9, TokenCost = 1000 };
+
+        Assert.Null(candidate.BestScore);
+        Assert.Equal(originalScore, candidate.BestScore);
+        Assert.Equal(0.9, updated.BestScore);
+        Assert.Equal(1000, updated.TokenCost);
+    }
+
+    [Fact]
+    public void HarnessSnapshot_SnapshotManifest_ContainsHashForEachSkillFile()
+    {
+        var snapshot = BuildSnapshot();
+
+        Assert.Equal(2, snapshot.SnapshotManifest.Count);
+        Assert.All(snapshot.SnapshotManifest, entry =>
+        {
+            Assert.False(string.IsNullOrEmpty(entry.Sha256Hash));
+            Assert.False(string.IsNullOrEmpty(entry.Path));
+        });
+    }
+}
diff --git a/src/Content/Tests/Infrastructure.AI.Tests/MetaHarness/ActiveConfigSnapshotBuilderTests.cs b/src/Content/Tests/Infrastructure.AI.Tests/MetaHarness/ActiveConfigSnapshotBuilderTests.cs
new file mode 100644
index 0000000..6a7777e
--- /dev/null
+++ b/src/Content/Tests/Infrastructure.AI.Tests/MetaHarness/ActiveConfigSnapshotBuilderTests.cs
@@ -0,0 +1,127 @@
+using Application.AI.Common.Interfaces;
+using Xunit;
+using Application.AI.Common.Interfaces.MetaHarness;
+using Domain.Common.Config.MetaHarness;
+using Infrastructure.AI.MetaHarness;
+using Microsoft.Extensions.Options;
+using Moq;
+using System.Security.Cryptography;
+using System.Text;
+
+namespace Infrastructure.AI.Tests.MetaHarness;
+
+/// <summary>
+/// Tests for ActiveConfigSnapshotBuilder: secret exclusion, SHA256 hashing, and redaction.
+/// </summary>
+public class ActiveConfigSnapshotBuilderTests : IDisposable
+{
+    private readonly string _tempDir;
+    private readonly Mock<ISecretRedactor> _redactorMock;
+    private readonly MetaHarnessConfig _config;
+
+    public ActiveConfigSnapshotBuilderTests()
+    {
+        _tempDir = Path.Combine(Path.GetTempPath(), $"harness-tests-{Guid.NewGuid():N}");
+        Directory.CreateDirectory(_tempDir);
+
+        _redactorMock = new Mock<ISecretRedactor>();
+        _redactorMock
+            .Setup(r => r.Redact(It.IsAny<string?>()))
+            .Returns<string?>(s => s);
+
+        _config = new MetaHarnessConfig
+        {
+            SnapshotConfigKeys = ["DatabaseName", "Region", "ApiKey"],
+            SecretsRedactionPatterns = ["Key", "Secret", "Token", "Password", "ConnectionString"]
+        };
+    }
+
+    public void Dispose() => Directory.Delete(_tempDir, recursive: true);
+
+    private ISnapshotBuilder BuildSut(MetaHarnessConfig? config = null)
+    {
+        var opts = Mock.Of<IOptionsMonitor<MetaHarnessConfig>>(
+            m => m.CurrentValue == (config ?? _config));
+        return new ActiveConfigSnapshotBuilder(opts, _redactorMock.Object);
+    }
+
+    [Fact]
+    public async Task Build_ExcludesSecretKeys_FromConfigSnapshot()
+    {
+        var sut = BuildSut();
+        var configValues = new Dictionary<string, string>
+        {
+            ["ApiKey"] = "super-secret",
+            ["DatabaseName"] = "mydb"
+        };
+
+        var snapshot = await sut.BuildAsync(_tempDir, "prompt", configValues);
+
+        Assert.DoesNotContain("ApiKey", snapshot.ConfigSnapshot.Keys);
+        Assert.Contains("DatabaseName", snapshot.ConfigSnapshot.Keys);
+    }
+
+    [Fact]
+    public async Task Build_IncludesAllowlistedConfigKeys()
+    {
+        var sut = BuildSut();
+        var configValues = new Dictionary<string, string>
+        {
+            ["DatabaseName"] = "mydb",
+            ["Region"] = "eastus",
+            ["Unrelated"] = "value"
+        };
+
+        var snapshot = await sut.BuildAsync(_tempDir, "prompt", configValues);
+
+        Assert.Contains("DatabaseName", snapshot.ConfigSnapshot.Keys);
+        Assert.Contains("Region", snapshot.ConfigSnapshot.Keys);
+        Assert.DoesNotContain("Unrelated", snapshot.ConfigSnapshot.Keys);
+    }
+
+    [Fact]
+    public async Task Build_ComputesSha256_ForEachSkillFile()
+    {
+        var file1 = Path.Combine(_tempDir, "SKILL.md");
+        var file2 = Path.Combine(_tempDir, "TOOL.md");
+        await File.WriteAllTextAsync(file1, "# Skill content");
+        await File.WriteAllTextAsync(file2, "# Tool content");
+
+        var sut = BuildSut();
+        var snapshot = await sut.BuildAsync(_tempDir, "prompt", new Dictionary<string, string>());
+
+        Assert.Equal(2, snapshot.SnapshotManifest.Count);
+        Assert.All(snapshot.SnapshotManifest, entry =>
+            Assert.False(string.IsNullOrEmpty(entry.Sha256Hash)));
+    }
+
+    [Fact]
+    public async Task Build_AppliesRedactor_ToSystemPrompt()
+    {
+        _redactorMock
+            .Setup(r => r.Redact("sensitive prompt"))
+            .Returns("[REDACTED]");
+
+        var sut = BuildSut();
+        var snapshot = await sut.BuildAsync(_tempDir, "sensitive prompt", new Dictionary<string, string>());
+
+        Assert.Equal("[REDACTED]", snapshot.SystemPromptSnapshot);
+    }
+
+    [Fact]
+    public async Task Build_SnapshotManifest_ContainsCorrectHashes()
+    {
+        const string content = "Hello, harness!";
+        var filePath = Path.Combine(_tempDir, "README.md");
+        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);
+
+        var expectedHash = Convert.ToHexString(
+            SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
+
+        var sut = BuildSut();
+        var snapshot = await sut.BuildAsync(_tempDir, "prompt", new Dictionary<string, string>());
+
+        var entry = Assert.Single(snapshot.SnapshotManifest);
+        Assert.Equal(expectedHash, entry.Sha256Hash);
+    }
+}
