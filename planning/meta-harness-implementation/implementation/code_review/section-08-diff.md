diff --git a/src/Content/Application/Application.AI.Common/Factories/AgentExecutionContextFactory.cs b/src/Content/Application/Application.AI.Common/Factories/AgentExecutionContextFactory.cs
index b47a5b9..320c23d 100644
--- a/src/Content/Application/Application.AI.Common/Factories/AgentExecutionContextFactory.cs
+++ b/src/Content/Application/Application.AI.Common/Factories/AgentExecutionContextFactory.cs
@@ -1,6 +1,7 @@
 using Application.AI.Common.Helpers;
 using Application.AI.Common.Interfaces;
 using Application.AI.Common.Interfaces.Context;
+using Application.AI.Common.Interfaces.Skills;
 using Application.AI.Common.Interfaces.Tools;
 using Application.AI.Common.Interfaces.Traces;
 using Domain.AI.Agents;
@@ -31,6 +32,7 @@ public class AgentExecutionContextFactory
 	private readonly IMcpToolProvider? _mcpToolProvider;
 	private readonly IContextBudgetTracker? _budgetTracker;
 	private readonly IExecutionTraceStore? _traceStore;
+	private readonly ISkillContentProvider? _skillContentProvider;
 
 	public AgentExecutionContextFactory(
 		ILogger<AgentExecutionContextFactory> logger,
@@ -40,7 +42,8 @@ public class AgentExecutionContextFactory
 		IToolConverter? toolConverter = null,
 		IMcpToolProvider? mcpToolProvider = null,
 		IContextBudgetTracker? budgetTracker = null,
-		IExecutionTraceStore? traceStore = null)
+		IExecutionTraceStore? traceStore = null,
+		ISkillContentProvider? skillContentProvider = null)
 	{
 		_logger = logger;
 		_appConfig = appConfig;
@@ -50,6 +53,7 @@ public class AgentExecutionContextFactory
 		_mcpToolProvider = mcpToolProvider;
 		_budgetTracker = budgetTracker;
 		_traceStore = traceStore;
+		_skillContentProvider = skillContentProvider;
 	}
 
 	/// <summary>
@@ -88,6 +92,10 @@ public class AgentExecutionContextFactory
 
 		var additionalProps = BuildAdditionalProperties(skill, options);
 
+		// Expose candidate skill content provider so evaluation contexts can inject candidate content
+		if (_skillContentProvider != null)
+			additionalProps[ISkillContentProvider.AdditionalPropertiesKey] = _skillContentProvider;
+
 		// Start a trace run when a store is wired in
 		if (_traceStore != null)
 		{
diff --git a/src/Content/Application/Application.AI.Common/Interfaces/Skills/ISkillContentProvider.cs b/src/Content/Application/Application.AI.Common/Interfaces/Skills/ISkillContentProvider.cs
new file mode 100644
index 0000000..908e155
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/Interfaces/Skills/ISkillContentProvider.cs
@@ -0,0 +1,18 @@
+namespace Application.AI.Common.Interfaces.Skills;
+
+/// <summary>
+/// Provides skill file content by path. Abstraction over filesystem and in-memory sources.
+/// Implementors return null when the requested path is not available from their source,
+/// allowing callers to fall back to alternative providers.
+/// </summary>
+public interface ISkillContentProvider
+{
+    /// <summary>Key used to store the provider in <c>AgentExecutionContext.AdditionalProperties</c>.</summary>
+    public const string AdditionalPropertiesKey = "__skillContentProvider";
+
+    /// <summary>
+    /// Returns the content of the skill file at <paramref name="skillPath"/>,
+    /// or null if this provider does not have content for that path.
+    /// </summary>
+    Task<string?> GetSkillContentAsync(string skillPath, CancellationToken cancellationToken = default);
+}
diff --git a/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs b/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs
index 904c9d7..dc656db 100644
--- a/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs
+++ b/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs
@@ -1,5 +1,6 @@
 using Application.AI.Common.Interfaces;
 using Application.AI.Common.Interfaces.A2A;
+using Application.AI.Common.Interfaces.Skills;
 using Application.AI.Common.Interfaces.Memory;
 using Application.AI.Common.Interfaces.Traces;
 using Infrastructure.AI.Memory;
@@ -122,6 +123,11 @@ public static class DependencyInjection
         services.AddSingleton<SkillMetadataParser>();
         services.AddSingleton<ISkillMetadataRegistry, SkillMetadataRegistry>();
 
+        // Skill content provider — default filesystem implementation for normal agent runs
+        // CandidateSkillContentProvider is NOT registered here; the evaluator constructs it
+        // directly with a HarnessCandidate snapshot for candidate-isolated evaluation.
+        services.AddTransient<ISkillContentProvider, FileSystemSkillContentProvider>();
+
         // Batched tool execution — parallel reads, serial writes
         services.AddSingleton<IToolConcurrencyClassifier, ToolConcurrencyClassifier>();
         services.AddTransient<IToolExecutionStrategy, BatchedToolExecutionStrategy>();
diff --git a/src/Content/Infrastructure/Infrastructure.AI/Skills/CandidateSkillContentProvider.cs b/src/Content/Infrastructure/Infrastructure.AI/Skills/CandidateSkillContentProvider.cs
new file mode 100644
index 0000000..f6f435d
--- /dev/null
+++ b/src/Content/Infrastructure/Infrastructure.AI/Skills/CandidateSkillContentProvider.cs
@@ -0,0 +1,29 @@
+using Application.AI.Common.Interfaces.Skills;
+
+namespace Infrastructure.AI.Skills;
+
+/// <summary>
+/// Serves skill content from an in-memory snapshot of a HarnessCandidate.
+/// Used during evaluation to isolate candidate skill content from the active filesystem state.
+/// Returns null for paths not present in the snapshot.
+/// </summary>
+public sealed class CandidateSkillContentProvider : ISkillContentProvider
+{
+    private readonly IReadOnlyDictionary<string, string> _skillFileSnapshots;
+
+    /// <param name="skillFileSnapshots">
+    /// Map of skill file path → content from the candidate's snapshot.
+    /// Typically <c>HarnessCandidate.Snapshot.SkillFileSnapshots</c>.
+    /// </param>
+    public CandidateSkillContentProvider(IReadOnlyDictionary<string, string> skillFileSnapshots)
+    {
+        _skillFileSnapshots = skillFileSnapshots;
+    }
+
+    /// <inheritdoc />
+    public Task<string?> GetSkillContentAsync(string skillPath, CancellationToken cancellationToken = default)
+    {
+        _skillFileSnapshots.TryGetValue(skillPath, out var content);
+        return Task.FromResult(content);
+    }
+}
diff --git a/src/Content/Infrastructure/Infrastructure.AI/Skills/FileSystemSkillContentProvider.cs b/src/Content/Infrastructure/Infrastructure.AI/Skills/FileSystemSkillContentProvider.cs
new file mode 100644
index 0000000..5b8b432
--- /dev/null
+++ b/src/Content/Infrastructure/Infrastructure.AI/Skills/FileSystemSkillContentProvider.cs
@@ -0,0 +1,19 @@
+using Application.AI.Common.Interfaces.Skills;
+
+namespace Infrastructure.AI.Skills;
+
+/// <summary>
+/// Reads skill content from the local filesystem.
+/// Returns null when the file does not exist.
+/// </summary>
+public sealed class FileSystemSkillContentProvider : ISkillContentProvider
+{
+    /// <inheritdoc />
+    public async Task<string?> GetSkillContentAsync(string skillPath, CancellationToken cancellationToken = default)
+    {
+        if (!File.Exists(skillPath))
+            return null;
+
+        return await File.ReadAllTextAsync(skillPath, cancellationToken);
+    }
+}
diff --git a/src/Content/Tests/Infrastructure.AI.Tests/Skills/SkillContentProviderTests.cs b/src/Content/Tests/Infrastructure.AI.Tests/Skills/SkillContentProviderTests.cs
new file mode 100644
index 0000000..6b068c0
--- /dev/null
+++ b/src/Content/Tests/Infrastructure.AI.Tests/Skills/SkillContentProviderTests.cs
@@ -0,0 +1,69 @@
+using FluentAssertions;
+using Infrastructure.AI.Skills;
+using Xunit;
+
+namespace Infrastructure.AI.Tests.Skills;
+
+/// <summary>
+/// Tests for ISkillContentProvider implementations.
+/// </summary>
+public sealed class SkillContentProviderTests
+{
+    [Fact]
+    public async Task CandidateSkillContentProvider_PathInSnapshot_ReturnsSnapshotContent()
+    {
+        var snapshot = new Dictionary<string, string>
+        {
+            ["skills/foo/SKILL.md"] = "# Foo content"
+        };
+        var provider = new CandidateSkillContentProvider(snapshot);
+
+        var result = await provider.GetSkillContentAsync("skills/foo/SKILL.md");
+
+        result.Should().Be("# Foo content");
+    }
+
+    [Fact]
+    public async Task CandidateSkillContentProvider_PathNotInSnapshot_ReturnsNull()
+    {
+        var snapshot = new Dictionary<string, string>
+        {
+            ["skills/foo/SKILL.md"] = "# Foo content"
+        };
+        var provider = new CandidateSkillContentProvider(snapshot);
+
+        var result = await provider.GetSkillContentAsync("skills/bar/SKILL.md");
+
+        result.Should().BeNull();
+    }
+
+    [Fact]
+    public async Task FileSystemSkillContentProvider_ExistingFile_ReturnsContent()
+    {
+        var tempFile = Path.GetTempFileName();
+        try
+        {
+            await File.WriteAllTextAsync(tempFile, "# Skill content");
+            var provider = new FileSystemSkillContentProvider();
+
+            var result = await provider.GetSkillContentAsync(tempFile);
+
+            result.Should().Be("# Skill content");
+        }
+        finally
+        {
+            File.Delete(tempFile);
+        }
+    }
+
+    [Fact]
+    public async Task FileSystemSkillContentProvider_NonExistentFile_ReturnsNull()
+    {
+        var provider = new FileSystemSkillContentProvider();
+        var nonExistentPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.md");
+
+        var result = await provider.GetSkillContentAsync(nonExistentPath);
+
+        result.Should().BeNull();
+    }
+}
