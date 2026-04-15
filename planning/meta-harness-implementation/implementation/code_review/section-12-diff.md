diff --git a/src/Content/Application/Application.AI.Common/Interfaces/MetaHarness/IEvaluationService.cs b/src/Content/Application/Application.AI.Common/Interfaces/MetaHarness/IEvaluationService.cs
new file mode 100644
index 0000000..46418ee
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/Interfaces/MetaHarness/IEvaluationService.cs
@@ -0,0 +1,33 @@
+using Domain.Common.MetaHarness;
+
+namespace Application.AI.Common.Interfaces.MetaHarness;
+
+/// <summary>
+/// Evaluates a harness candidate against a set of tasks and returns aggregated scores.
+/// Each task is run in isolation using the candidate's in-memory skill snapshots.
+/// </summary>
+public interface IEvaluationService
+{
+    /// <summary>
+    /// Runs each eval task against the candidate's proposed harness configuration,
+    /// grades outputs against expected patterns, and writes per-task traces.
+    /// </summary>
+    Task<EvaluationResult> EvaluateAsync(
+        HarnessCandidate candidate,
+        IReadOnlyList<EvalTask> evalTasks,
+        CancellationToken cancellationToken = default);
+}
+
+/// <summary>Aggregated result of evaluating one candidate across all tasks.</summary>
+public sealed record EvaluationResult(
+    Guid CandidateId,
+    double PassRate,
+    long TotalTokenCost,
+    IReadOnlyList<TaskEvaluationResult> PerExampleResults);
+
+/// <summary>Result for a single eval task run.</summary>
+public sealed record TaskEvaluationResult(
+    string TaskId,
+    bool Passed,
+    long TokenCost,
+    string? FailureReason = null);
diff --git a/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs b/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs
index 9fb1c23..ea99c01 100644
--- a/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs
+++ b/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs
@@ -195,6 +195,9 @@ public static class DependencyInjection
         // Harness proposer — scoped because each invocation carries iteration-specific context
         services.AddScoped<IHarnessProposer, OrchestratedHarnessProposer>();
 
+        // Harness evaluator — scoped; each evaluation creates its own SemaphoreSlim
+        services.AddScoped<IEvaluationService, AgentEvaluationService>();
+
         return services;
     }
 
diff --git a/src/Content/Infrastructure/Infrastructure.AI/MetaHarness/AgentEvaluationService.cs b/src/Content/Infrastructure/Infrastructure.AI/MetaHarness/AgentEvaluationService.cs
new file mode 100644
index 0000000..bd78893
--- /dev/null
+++ b/src/Content/Infrastructure/Infrastructure.AI/MetaHarness/AgentEvaluationService.cs
@@ -0,0 +1,197 @@
+using System.Text.RegularExpressions;
+using Application.AI.Common.Interfaces;
+using Application.AI.Common.Interfaces.MetaHarness;
+using Application.AI.Common.Interfaces.Skills;
+using Application.AI.Common.Interfaces.Traces;
+using Domain.AI.Agents;
+using Domain.Common.Config.MetaHarness;
+using Domain.Common.MetaHarness;
+using Infrastructure.AI.Skills;
+using Microsoft.Agents.AI;
+using Microsoft.Extensions.AI;
+using Microsoft.Extensions.Logging;
+using Microsoft.Extensions.Options;
+
+namespace Infrastructure.AI.MetaHarness;
+
+/// <summary>
+/// Evaluates a harness candidate by running each eval task against the candidate's
+/// in-memory skill snapshot, grading outputs via regex, and writing per-task traces.
+/// </summary>
+/// <remarks>
+/// Registered as <c>Scoped</c> — each evaluation creates its own <see cref="SemaphoreSlim"/>
+/// scoped to the current optimization loop iteration.
+/// </remarks>
+public sealed class AgentEvaluationService : IEvaluationService
+{
+    private readonly IOptionsMonitor<MetaHarnessConfig> _config;
+    private readonly IExecutionTraceStore _traceStore;
+    private readonly IAgentFactory _agentFactory;
+    private readonly ILogger<AgentEvaluationService> _logger;
+
+    public AgentEvaluationService(
+        IOptionsMonitor<MetaHarnessConfig> config,
+        IExecutionTraceStore traceStore,
+        IAgentFactory agentFactory,
+        ILogger<AgentEvaluationService> logger)
+    {
+        _config = config;
+        _traceStore = traceStore;
+        _agentFactory = agentFactory;
+        _logger = logger;
+    }
+
+    /// <inheritdoc />
+    public async Task<EvaluationResult> EvaluateAsync(
+        HarnessCandidate candidate,
+        IReadOnlyList<EvalTask> evalTasks,
+        CancellationToken cancellationToken = default)
+    {
+        var cfg = _config.CurrentValue;
+        using var semaphore = new SemaphoreSlim(cfg.MaxEvalParallelism, cfg.MaxEvalParallelism);
+
+        var taskResults = await Task.WhenAll(
+            evalTasks.Select(task => RunSingleTaskAsync(candidate, task, cfg, semaphore, cancellationToken)));
+
+        var passed = taskResults.Count(r => r.Passed);
+        var passRate = evalTasks.Count > 0 ? (double)passed / evalTasks.Count : 0.0;
+        var totalTokenCost = taskResults.Sum(r => r.TokenCost);
+
+        _logger.LogInformation(
+            "Candidate {CandidateId}: {Passed}/{Total} tasks passed (PassRate={PassRate:F2})",
+            candidate.CandidateId, passed, evalTasks.Count, passRate);
+
+        return new EvaluationResult(candidate.CandidateId, passRate, totalTokenCost, taskResults);
+    }
+
+    private async Task<TaskEvaluationResult> RunSingleTaskAsync(
+        HarnessCandidate candidate,
+        EvalTask task,
+        MetaHarnessConfig cfg,
+        SemaphoreSlim semaphore,
+        CancellationToken cancellationToken)
+    {
+        await semaphore.WaitAsync(cancellationToken);
+        try
+        {
+            return await ExecuteTaskAsync(candidate, task, cfg, cancellationToken);
+        }
+        finally
+        {
+            semaphore.Release();
+        }
+    }
+
+    private async Task<TaskEvaluationResult> ExecuteTaskAsync(
+        HarnessCandidate candidate,
+        EvalTask task,
+        MetaHarnessConfig cfg,
+        CancellationToken cancellationToken)
+    {
+        var scope = new TraceScope
+        {
+            ExecutionRunId = Guid.NewGuid(),
+            OptimizationRunId = candidate.OptimizationRunId,
+            CandidateId = candidate.CandidateId,
+            TaskId = task.TaskId
+        };
+
+        var candidateProvider = new CandidateSkillContentProvider(candidate.Snapshot.SkillFileSnapshots);
+
+        var metadata = new RunMetadata
+        {
+            AgentName = "EvaluationAgent",
+            StartedAt = DateTimeOffset.UtcNow
+        };
+
+        ITraceWriter? traceWriter = null;
+
+        try
+        {
+            traceWriter = await _traceStore.StartRunAsync(scope, metadata, cancellationToken);
+
+            var context = new AgentExecutionContext
+            {
+                Name = "EvaluationAgent",
+                Instruction = candidate.Snapshot.SystemPromptSnapshot,
+                DeploymentName = string.IsNullOrEmpty(cfg.EvaluationModelVersion) ? null : cfg.EvaluationModelVersion,
+                TraceScope = scope,
+                AdditionalProperties = new Dictionary<string, object>
+                {
+                    [ISkillContentProvider.AdditionalPropertiesKey] = candidateProvider,
+                    [ITraceWriter.AdditionalPropertiesKey] = traceWriter
+                }
+            };
+
+            var agent = await _agentFactory.CreateAgentAsync(context, cancellationToken);
+            var response = await agent.RunAsync(
+                [new ChatMessage(ChatRole.User, task.InputPrompt)],
+                cancellationToken: cancellationToken);
+
+            var output = ExtractContent(response);
+            var (passed, failureReason) = Grade(output, task.ExpectedOutputPattern);
+
+            await traceWriter.CompleteAsync(cancellationToken);
+
+            return new TaskEvaluationResult(task.TaskId, passed, TokenCost: 0L, failureReason);
+        }
+        catch (Exception ex) when (ex is not OperationCanceledException)
+        {
+            _logger.LogError(ex, "Task {TaskId} failed for candidate {CandidateId}",
+                task.TaskId, candidate.CandidateId);
+
+            if (traceWriter is not null)
+            {
+                try { await traceWriter.CompleteAsync(cancellationToken); }
+                catch (Exception completionEx)
+                {
+                    _logger.LogWarning(completionEx, "Failed to complete trace for failed task {TaskId}", task.TaskId);
+                }
+            }
+
+            return new TaskEvaluationResult(task.TaskId, Passed: false, TokenCost: 0L, ex.Message);
+        }
+        finally
+        {
+            if (traceWriter is not null)
+                await traceWriter.DisposeAsync();
+        }
+    }
+
+    private static (bool Passed, string? FailureReason) Grade(string output, string? pattern)
+    {
+        if (pattern is null)
+            return (true, null);
+
+        try
+        {
+            var match = Regex.Match(output, pattern, RegexOptions.None, TimeSpan.FromSeconds(5));
+            return match.Success ? (true, null) : (false, "pattern_not_matched");
+        }
+        catch (RegexMatchTimeoutException)
+        {
+            return (false, "regex_timeout");
+        }
+    }
+
+    private static string ExtractContent(object? response)
+    {
+        if (response is null)
+            return string.Empty;
+        if (response is string str)
+            return str;
+        if (response is AgentResponse agentResponse)
+            return agentResponse.Text ?? string.Empty;
+        if (response is ChatResponse chatResponse)
+        {
+            return string.Join("\n", chatResponse.Messages
+                .Where(m => m.Role == ChatRole.Assistant)
+                .SelectMany(m => m.Contents.OfType<TextContent>())
+                .Select(tc => tc.Text));
+        }
+
+        return response.GetType().GetProperty("Content")?.GetValue(response)?.ToString()
+            ?? response.ToString()
+            ?? string.Empty;
+    }
+}
diff --git a/src/Content/Infrastructure/Infrastructure.AI/MetaHarness/EvalTaskLoader.cs b/src/Content/Infrastructure/Infrastructure.AI/MetaHarness/EvalTaskLoader.cs
new file mode 100644
index 0000000..1ad5e2b
--- /dev/null
+++ b/src/Content/Infrastructure/Infrastructure.AI/MetaHarness/EvalTaskLoader.cs
@@ -0,0 +1,53 @@
+using System.Text.Json;
+using Domain.Common.MetaHarness;
+using Microsoft.Extensions.Logging;
+
+namespace Infrastructure.AI.MetaHarness;
+
+/// <summary>
+/// Loads <see cref="EvalTask"/> definitions from JSON files at the configured path.
+/// Each file must deserialize to a single <see cref="EvalTask"/>.
+/// Files that fail to deserialize are logged and skipped.
+/// </summary>
+public static class EvalTaskLoader
+{
+    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
+
+    /// <summary>
+    /// Reads all <c>*.json</c> files under <paramref name="directoryPath"/> and deserializes
+    /// each as an <see cref="EvalTask"/>. Files that fail to deserialize are logged and skipped.
+    /// Returns an empty list if the directory does not exist.
+    /// </summary>
+    /// <param name="directoryPath">Path to the directory containing eval task JSON files.</param>
+    /// <param name="logger">Logger for warnings on missing directory or parse failures.</param>
+    public static IReadOnlyList<EvalTask> LoadFromDirectory(string directoryPath, ILogger logger)
+    {
+        if (!Directory.Exists(directoryPath))
+        {
+            logger.LogWarning("Eval tasks directory not found: {Path}", directoryPath);
+            return [];
+        }
+
+        var results = new List<EvalTask>();
+
+        foreach (var file in Directory.EnumerateFiles(directoryPath, "*.json", SearchOption.TopDirectoryOnly))
+        {
+            try
+            {
+                var json = File.ReadAllText(file);
+                var task = JsonSerializer.Deserialize<EvalTask>(json, JsonOptions);
+                if (task is not null)
+                    results.Add(task);
+                else
+                    logger.LogWarning("Eval task file deserialized as null, skipping: {File}", file);
+            }
+            catch (Exception ex)
+            {
+                logger.LogWarning(ex, "Failed to deserialize eval task from {File}, skipping", file);
+            }
+        }
+
+        logger.LogInformation("Loaded {Count} eval task(s) from {Path}", results.Count, directoryPath);
+        return results;
+    }
+}
diff --git a/src/Content/Tests/Infrastructure.AI.Tests/Helpers/TestableAIAgent.cs b/src/Content/Tests/Infrastructure.AI.Tests/Helpers/TestableAIAgent.cs
new file mode 100644
index 0000000..bf48895
--- /dev/null
+++ b/src/Content/Tests/Infrastructure.AI.Tests/Helpers/TestableAIAgent.cs
@@ -0,0 +1,76 @@
+using System.Runtime.CompilerServices;
+using System.Text.Json;
+using Microsoft.Agents.AI;
+using Microsoft.Extensions.AI;
+
+namespace Infrastructure.AI.Tests.Helpers;
+
+/// <summary>
+/// A concrete AIAgent subclass for testing. Overrides the abstract core methods
+/// so tests can control agent behavior without external dependencies.
+/// </summary>
+public sealed class TestableAIAgent : AIAgent
+{
+    private readonly Func<IEnumerable<ChatMessage>, CancellationToken, Task<AgentResponse>> _runHandler;
+
+    public TestableAIAgent(string responseText)
+        : this(_ => new AgentResponse(new ChatMessage(ChatRole.Assistant, responseText)))
+    {
+    }
+
+    public TestableAIAgent(Func<IEnumerable<ChatMessage>, AgentResponse> handler)
+        : this((msgs, _) => Task.FromResult(handler(msgs)))
+    {
+    }
+
+    public TestableAIAgent(Func<IEnumerable<ChatMessage>, CancellationToken, Task<AgentResponse>> handler)
+    {
+        _runHandler = handler;
+    }
+
+    /// <summary>Creates a TestableAIAgent that delays before returning, for parallelism tests.</summary>
+    public static TestableAIAgent WithDelay(string responseText, TimeSpan delay)
+    {
+        return new TestableAIAgent(async (_, ct) =>
+        {
+            await Task.Delay(delay, ct);
+            return new AgentResponse(new ChatMessage(ChatRole.Assistant, responseText));
+        });
+    }
+
+    /// <summary>Creates a TestableAIAgent that throws the given exception on RunAsync.</summary>
+    public static TestableAIAgent Throwing(Exception exception)
+        => new TestableAIAgent((_, _) => throw exception);
+
+    protected override Task<AgentResponse> RunCoreAsync(
+        IEnumerable<ChatMessage> messages,
+        AgentSession? session,
+        AgentRunOptions? options,
+        CancellationToken cancellationToken)
+        => _runHandler(messages, cancellationToken);
+
+    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
+        IEnumerable<ChatMessage> messages,
+        AgentSession? session,
+        AgentRunOptions? options,
+        [EnumeratorCancellation] CancellationToken cancellationToken)
+    {
+        var response = await _runHandler(messages, cancellationToken);
+        yield return new AgentResponseUpdate(ChatRole.Assistant, response.Text ?? string.Empty);
+    }
+
+    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
+        => ValueTask.FromResult<AgentSession>(new TestableAgentSession());
+
+    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
+        AgentSession session,
+        JsonSerializerOptions? jsonSerializerOptions,
+        CancellationToken cancellationToken)
+        => ValueTask.FromResult(JsonDocument.Parse("{}").RootElement);
+
+    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
+        JsonElement serializedState,
+        JsonSerializerOptions? jsonSerializerOptions,
+        CancellationToken cancellationToken)
+        => ValueTask.FromResult<AgentSession>(new TestableAgentSession());
+}
diff --git a/src/Content/Tests/Infrastructure.AI.Tests/Helpers/TestableAgentSession.cs b/src/Content/Tests/Infrastructure.AI.Tests/Helpers/TestableAgentSession.cs
new file mode 100644
index 0000000..5b3cf5c
--- /dev/null
+++ b/src/Content/Tests/Infrastructure.AI.Tests/Helpers/TestableAgentSession.cs
@@ -0,0 +1,8 @@
+using Microsoft.Agents.AI;
+
+namespace Infrastructure.AI.Tests.Helpers;
+
+/// <summary>Minimal concrete AgentSession for test use.</summary>
+public sealed class TestableAgentSession : AgentSession
+{
+}
diff --git a/src/Content/Tests/Infrastructure.AI.Tests/MetaHarness/AgentEvaluationServiceTests.cs b/src/Content/Tests/Infrastructure.AI.Tests/MetaHarness/AgentEvaluationServiceTests.cs
new file mode 100644
index 0000000..b6298d5
--- /dev/null
+++ b/src/Content/Tests/Infrastructure.AI.Tests/MetaHarness/AgentEvaluationServiceTests.cs
@@ -0,0 +1,262 @@
+using System.Diagnostics;
+using Application.AI.Common.Interfaces;
+using Application.AI.Common.Interfaces.MetaHarness;
+using Application.AI.Common.Interfaces.Skills;
+using Application.AI.Common.Interfaces.Traces;
+using Domain.AI.Agents;
+using Domain.Common.Config;
+using Domain.Common.Config.MetaHarness;
+using Domain.Common.MetaHarness;
+using Infrastructure.AI.MetaHarness;
+using Infrastructure.AI.Security;
+using Infrastructure.AI.Skills;
+using Infrastructure.AI.Tests.Helpers;
+using Infrastructure.AI.Traces;
+using Microsoft.Extensions.Logging.Abstractions;
+using Microsoft.Extensions.Options;
+using Moq;
+using Xunit;
+
+namespace Infrastructure.AI.Tests.MetaHarness;
+
+/// <summary>
+/// Tests for AgentEvaluationService scoring, grading, tracing, and parallelism.
+/// Uses TestableAIAgent to control agent output without external LLM dependencies.
+/// </summary>
+public class AgentEvaluationServiceTests : IAsyncDisposable
+{
+    private readonly Mock<IAgentFactory> _agentFactoryMock = new();
+    private readonly string _traceRoot = Path.Combine(Path.GetTempPath(), $"eval-tests-{Guid.NewGuid():N}");
+
+    private AgentEvaluationService BuildSut(MetaHarnessConfig? config = null)
+    {
+        var cfg = config ?? new MetaHarnessConfig { TraceDirectoryRoot = _traceRoot };
+        var opts = Mock.Of<IOptionsMonitor<MetaHarnessConfig>>(m => m.CurrentValue == cfg);
+        var traceStore = BuildTraceStore(cfg.TraceDirectoryRoot);
+        return new AgentEvaluationService(opts, traceStore, _agentFactoryMock.Object,
+            NullLogger<AgentEvaluationService>.Instance);
+    }
+
+    private IExecutionTraceStore BuildTraceStore(string traceRoot)
+    {
+        var appCfg = new AppConfig
+        {
+            MetaHarness = new MetaHarnessConfig { TraceDirectoryRoot = traceRoot }
+        };
+        var appOpts = Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == appCfg);
+        var redactor = new PatternSecretRedactor(
+            Mock.Of<IOptionsMonitor<MetaHarnessConfig>>(m => m.CurrentValue == new MetaHarnessConfig()));
+        return new FileSystemExecutionTraceStore(appOpts, redactor,
+            NullLogger<FileSystemExecutionTraceStore>.Instance);
+    }
+
+    private static HarnessCandidate BuildCandidate(
+        Guid? optRunId = null,
+        string systemPrompt = "You are a helpful assistant.",
+        Dictionary<string, string>? skillFiles = null) =>
+        new()
+        {
+            CandidateId = Guid.NewGuid(),
+            OptimizationRunId = optRunId ?? Guid.NewGuid(),
+            Iteration = 0,
+            CreatedAt = DateTimeOffset.UtcNow,
+            Status = HarnessCandidateStatus.Proposed,
+            Snapshot = new HarnessSnapshot
+            {
+                SkillFileSnapshots = skillFiles ?? new Dictionary<string, string>(),
+                SystemPromptSnapshot = systemPrompt,
+                ConfigSnapshot = new Dictionary<string, string>(),
+                SnapshotManifest = []
+            }
+        };
+
+    private static EvalTask BuildTask(string taskId, string prompt, string? pattern = null) =>
+        new()
+        {
+            TaskId = taskId,
+            Description = taskId,
+            InputPrompt = prompt,
+            ExpectedOutputPattern = pattern
+        };
+
+    /// <summary>All tasks match their expected output patterns. PassRate should equal 1.0.</summary>
+    [Fact]
+    public async Task EvaluateAsync_AllTasksPass_ReturnsPassRateOne()
+    {
+        _agentFactoryMock
+            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(new TestableAIAgent("The answer is 42"));
+
+        var sut = BuildSut();
+        var candidate = BuildCandidate();
+        var tasks = new[]
+        {
+            BuildTask("t1", "question 1", pattern: "answer"),
+            BuildTask("t2", "question 2", pattern: "42")
+        };
+
+        var result = await sut.EvaluateAsync(candidate, tasks);
+
+        Assert.Equal(1.0, result.PassRate);
+        Assert.All(result.PerExampleResults, r => Assert.True(r.Passed));
+    }
+
+    /// <summary>No tasks match their expected output patterns. PassRate should equal 0.0.</summary>
+    [Fact]
+    public async Task EvaluateAsync_AllTasksFail_ReturnsPassRateZero()
+    {
+        _agentFactoryMock
+            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(new TestableAIAgent("completely unrelated output"));
+
+        var sut = BuildSut();
+        var candidate = BuildCandidate();
+        var tasks = new[]
+        {
+            BuildTask("t1", "question 1", pattern: "^expected answer$"),
+            BuildTask("t2", "question 2", pattern: "^also expected$")
+        };
+
+        var result = await sut.EvaluateAsync(candidate, tasks);
+
+        Assert.Equal(0.0, result.PassRate);
+        Assert.All(result.PerExampleResults, r => Assert.False(r.Passed));
+    }
+
+    /// <summary>
+    /// Catastrophic backtracking regex triggers RegexMatchTimeoutException.
+    /// Task must be recorded as Passed=false with FailureReason="regex_timeout".
+    /// </summary>
+    [Fact]
+    public async Task EvaluateAsync_RegexTimeout_CountsAsFailNotError()
+    {
+        // Catastrophic backtracking: ^(a+)+$ on a long "aaaa...b" string reliably triggers timeout
+        const string catastrophicPattern = "^(a+)+$";
+        var longInput = new string('a', 30) + "b";
+
+        _agentFactoryMock
+            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(new TestableAIAgent(longInput));
+
+        var sut = BuildSut();
+        var candidate = BuildCandidate();
+        var tasks = new[] { BuildTask("timeout-task", "any prompt", pattern: catastrophicPattern) };
+
+        var result = await sut.EvaluateAsync(candidate, tasks);
+
+        Assert.Equal(0.0, result.PassRate);
+        var taskResult = Assert.Single(result.PerExampleResults);
+        Assert.False(taskResult.Passed);
+        Assert.Equal("regex_timeout", taskResult.FailureReason);
+    }
+
+    /// <summary>
+    /// After evaluation, trace directory must exist under:
+    ///   optimizations/{optRunId}/candidates/{candidateId}/eval/{taskId}/{executionRunId}/
+    /// Verify that manifest.json exists in that path.
+    /// </summary>
+    [Fact]
+    public async Task EvaluateAsync_WritesTraceUnderCandidateEvalDirectory()
+    {
+        var optRunId = Guid.NewGuid();
+        _agentFactoryMock
+            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(new TestableAIAgent("trace output"));
+
+        var sut = BuildSut();
+        var candidate = BuildCandidate(optRunId: optRunId);
+        var tasks = new[] { BuildTask("trace-task", "prompt", pattern: null) };
+
+        var result = await sut.EvaluateAsync(candidate, tasks);
+
+        Assert.Equal(1.0, result.PassRate);
+
+        // Verify trace directory structure
+        var expectedCandidateDir = Path.Combine(
+            _traceRoot, "optimizations",
+            optRunId.ToString("D").ToLowerInvariant(),
+            "candidates",
+            candidate.CandidateId.ToString("D").ToLowerInvariant(),
+            "eval", "trace-task");
+
+        Assert.True(Directory.Exists(expectedCandidateDir),
+            $"Eval directory not found: {expectedCandidateDir}");
+
+        // At least one execution run directory with manifest.json
+        var runDirs = Directory.GetDirectories(expectedCandidateDir);
+        Assert.NotEmpty(runDirs);
+        Assert.Contains(runDirs, d => File.Exists(Path.Combine(d, "manifest.json")));
+    }
+
+    /// <summary>
+    /// The context passed to CreateAgentAsync must have AdditionalProperties containing
+    /// a CandidateSkillContentProvider, not a filesystem-backed provider.
+    /// </summary>
+    [Fact]
+    public async Task EvaluateAsync_UsesCandidateSkillContentProvider_NotFilesystem()
+    {
+        AgentExecutionContext? capturedContext = null;
+
+        _agentFactoryMock
+            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
+            .Callback<AgentExecutionContext, CancellationToken>((ctx, _) => capturedContext = ctx)
+            .ReturnsAsync(new TestableAIAgent("output"));
+
+        var sut = BuildSut();
+        var skillFiles = new Dictionary<string, string> { ["SKILL.md"] = "# Skill content" };
+        var candidate = BuildCandidate(skillFiles: skillFiles);
+        var tasks = new[] { BuildTask("provider-task", "prompt", pattern: null) };
+
+        await sut.EvaluateAsync(candidate, tasks);
+
+        Assert.NotNull(capturedContext);
+        Assert.NotNull(capturedContext.AdditionalProperties);
+        Assert.True(capturedContext.AdditionalProperties.TryGetValue(
+            ISkillContentProvider.AdditionalPropertiesKey, out var provider));
+        Assert.IsType<CandidateSkillContentProvider>(provider);
+    }
+
+    /// <summary>
+    /// With MaxEvalParallelism=2 and 4 tasks each with 50ms delay,
+    /// total elapsed time should be ~100ms (2 batches), not ~200ms (sequential).
+    /// </summary>
+    [Fact]
+    public async Task EvaluateAsync_WithParallelism2_RunsTasksConcurrently()
+    {
+        _agentFactoryMock
+            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(() => TestableAIAgent.WithDelay("ok", TimeSpan.FromMilliseconds(50)));
+
+        var cfg = new MetaHarnessConfig
+        {
+            MaxEvalParallelism = 2,
+            TraceDirectoryRoot = _traceRoot
+        };
+        var sut = BuildSut(cfg);
+        var candidate = BuildCandidate();
+        var tasks = Enumerable.Range(1, 4)
+            .Select(i => BuildTask($"t{i}", $"prompt {i}", pattern: null))
+            .ToArray();
+
+        var sw = Stopwatch.StartNew();
+        var result = await sut.EvaluateAsync(candidate, tasks);
+        sw.Stop();
+
+        Assert.Equal(1.0, result.PassRate);
+        // 2 parallel × 2 batches = ~100ms; allow 30ms tolerance on each side
+        Assert.True(sw.ElapsedMilliseconds < 200,
+            $"Expected <200ms with parallelism=2 but took {sw.ElapsedMilliseconds}ms");
+        Assert.True(sw.ElapsedMilliseconds >= 70,
+            $"Expected >=70ms (2 batches) but took {sw.ElapsedMilliseconds}ms");
+    }
+
+    public async ValueTask DisposeAsync()
+    {
+        if (Directory.Exists(_traceRoot))
+        {
+            try { Directory.Delete(_traceRoot, recursive: true); }
+            catch { /* best effort cleanup */ }
+        }
+        await ValueTask.CompletedTask;
+    }
+}
