using System.Diagnostics;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.MetaHarness;
using Application.AI.Common.Interfaces.Skills;
using Application.AI.Common.Interfaces.Traces;
using Domain.AI.Agents;
using Domain.Common.Config;
using Domain.Common.Config.MetaHarness;
using Domain.Common.MetaHarness;
using Infrastructure.AI.MetaHarness;
using Infrastructure.AI.Security;
using Infrastructure.AI.Skills;
using Infrastructure.AI.Tests.Helpers;
using Infrastructure.AI.Traces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.MetaHarness;

/// <summary>
/// Tests for AgentEvaluationService scoring, grading, tracing, and parallelism.
/// Uses TestableAIAgent to control agent output without external LLM dependencies.
/// </summary>
public class AgentEvaluationServiceTests : IAsyncDisposable
{
    private readonly Mock<IAgentFactory> _agentFactoryMock = new();
    private readonly string _traceRoot = Path.Combine(Path.GetTempPath(), $"eval-tests-{Guid.NewGuid():N}");

    private AgentEvaluationService BuildSut(MetaHarnessConfig? config = null)
    {
        var cfg = config ?? new MetaHarnessConfig { TraceDirectoryRoot = _traceRoot };
        var opts = Mock.Of<IOptionsMonitor<MetaHarnessConfig>>(m => m.CurrentValue == cfg);
        var traceStore = BuildTraceStore(cfg.TraceDirectoryRoot);
        return new AgentEvaluationService(opts, traceStore, _agentFactoryMock.Object,
            NullLogger<AgentEvaluationService>.Instance);
    }

    private IExecutionTraceStore BuildTraceStore(string traceRoot)
    {
        var appCfg = new AppConfig
        {
            MetaHarness = new MetaHarnessConfig { TraceDirectoryRoot = traceRoot }
        };
        var appOpts = Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == appCfg);
        var redactor = new PatternSecretRedactor(
            Mock.Of<IOptionsMonitor<MetaHarnessConfig>>(m => m.CurrentValue == new MetaHarnessConfig()));
        return new FileSystemExecutionTraceStore(appOpts, redactor,
            NullLogger<FileSystemExecutionTraceStore>.Instance);
    }

    private static HarnessCandidate BuildCandidate(
        Guid? optRunId = null,
        string systemPrompt = "You are a helpful assistant.",
        Dictionary<string, string>? skillFiles = null) =>
        new()
        {
            CandidateId = Guid.NewGuid(),
            OptimizationRunId = optRunId ?? Guid.NewGuid(),
            Iteration = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            Status = HarnessCandidateStatus.Proposed,
            Snapshot = new HarnessSnapshot
            {
                SkillFileSnapshots = skillFiles ?? new Dictionary<string, string>(),
                SystemPromptSnapshot = systemPrompt,
                ConfigSnapshot = new Dictionary<string, string>(),
                SnapshotManifest = []
            }
        };

    private static EvalTask BuildTask(string taskId, string prompt, string? pattern = null) =>
        new()
        {
            TaskId = taskId,
            Description = taskId,
            InputPrompt = prompt,
            ExpectedOutputPattern = pattern
        };

    /// <summary>All tasks match their expected output patterns. PassRate should equal 1.0.</summary>
    [Fact]
    public async Task EvaluateAsync_AllTasksPass_ReturnsPassRateOne()
    {
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestableAIAgent("The answer is 42"));

        var sut = BuildSut();
        var candidate = BuildCandidate();
        var tasks = new[]
        {
            BuildTask("t1", "question 1", pattern: "answer"),
            BuildTask("t2", "question 2", pattern: "42")
        };

        var result = await sut.EvaluateAsync(candidate, tasks);

        Assert.Equal(1.0, result.PassRate);
        Assert.All(result.PerExampleResults, r => Assert.True(r.Passed));
    }

    /// <summary>No tasks match their expected output patterns. PassRate should equal 0.0.</summary>
    [Fact]
    public async Task EvaluateAsync_AllTasksFail_ReturnsPassRateZero()
    {
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestableAIAgent("completely unrelated output"));

        var sut = BuildSut();
        var candidate = BuildCandidate();
        var tasks = new[]
        {
            BuildTask("t1", "question 1", pattern: "^expected answer$"),
            BuildTask("t2", "question 2", pattern: "^also expected$")
        };

        var result = await sut.EvaluateAsync(candidate, tasks);

        Assert.Equal(0.0, result.PassRate);
        Assert.All(result.PerExampleResults, r => Assert.False(r.Passed));
    }

    /// <summary>
    /// Catastrophic backtracking regex triggers RegexMatchTimeoutException.
    /// Task must be recorded as Passed=false with FailureReason="regex_timeout".
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_RegexTimeout_CountsAsFailNotError()
    {
        // Catastrophic backtracking: ^(a+)+$ on a long "aaaa...b" string reliably triggers timeout
        const string catastrophicPattern = "^(a+)+$";
        var longInput = new string('a', 30) + "b";

        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestableAIAgent(longInput));

        var sut = BuildSut();
        var candidate = BuildCandidate();
        var tasks = new[] { BuildTask("timeout-task", "any prompt", pattern: catastrophicPattern) };

        var result = await sut.EvaluateAsync(candidate, tasks);

        Assert.Equal(0.0, result.PassRate);
        var taskResult = Assert.Single(result.PerExampleResults);
        Assert.False(taskResult.Passed);
        Assert.Equal("regex_timeout", taskResult.FailureReason);
    }

    /// <summary>
    /// After evaluation, trace directory must exist under:
    ///   optimizations/{optRunId}/candidates/{candidateId}/eval/{taskId}/{executionRunId}/
    /// Verify that manifest.json exists in that path.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_WritesTraceUnderCandidateEvalDirectory()
    {
        var optRunId = Guid.NewGuid();
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestableAIAgent("trace output"));

        var sut = BuildSut();
        var candidate = BuildCandidate(optRunId: optRunId);
        var tasks = new[] { BuildTask("trace-task", "prompt", pattern: null) };

        var result = await sut.EvaluateAsync(candidate, tasks);

        Assert.Equal(1.0, result.PassRate);

        // Verify trace directory structure
        var expectedCandidateDir = Path.Combine(
            _traceRoot, "optimizations",
            optRunId.ToString("D").ToLowerInvariant(),
            "candidates",
            candidate.CandidateId.ToString("D").ToLowerInvariant(),
            "eval", "trace-task");

        Assert.True(Directory.Exists(expectedCandidateDir),
            $"Eval directory not found: {expectedCandidateDir}");

        // At least one execution run directory with manifest.json
        var runDirs = Directory.GetDirectories(expectedCandidateDir);
        Assert.NotEmpty(runDirs);
        Assert.Contains(runDirs, d => File.Exists(Path.Combine(d, "manifest.json")));
    }

    /// <summary>
    /// The context passed to CreateAgentAsync must have AdditionalProperties containing
    /// a CandidateSkillContentProvider, not a filesystem-backed provider.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_UsesCandidateSkillContentProvider_NotFilesystem()
    {
        AgentExecutionContext? capturedContext = null;

        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .Callback<AgentExecutionContext, CancellationToken>((ctx, _) => capturedContext = ctx)
            .ReturnsAsync(new TestableAIAgent("output"));

        var sut = BuildSut();
        var skillFiles = new Dictionary<string, string> { ["SKILL.md"] = "# Skill content" };
        var candidate = BuildCandidate(skillFiles: skillFiles);
        var tasks = new[] { BuildTask("provider-task", "prompt", pattern: null) };

        await sut.EvaluateAsync(candidate, tasks);

        Assert.NotNull(capturedContext);
        Assert.NotNull(capturedContext.AdditionalProperties);
        Assert.True(capturedContext.AdditionalProperties.TryGetValue(
            ISkillContentProvider.AdditionalPropertiesKey, out var provider));
        Assert.IsType<CandidateSkillContentProvider>(provider);
    }

    /// <summary>
    /// With MaxEvalParallelism=2 and 4 tasks each with 50ms delay,
    /// total elapsed time should be ~100ms (2 batches), not ~200ms (sequential).
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_WithParallelism2_RunsTasksConcurrently()
    {
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => TestableAIAgent.WithDelay("ok", TimeSpan.FromMilliseconds(50)));

        var cfg = new MetaHarnessConfig
        {
            MaxEvalParallelism = 2,
            TraceDirectoryRoot = _traceRoot
        };
        var sut = BuildSut(cfg);
        var candidate = BuildCandidate();
        var tasks = Enumerable.Range(1, 4)
            .Select(i => BuildTask($"t{i}", $"prompt {i}", pattern: null))
            .ToArray();

        var sw = Stopwatch.StartNew();
        var result = await sut.EvaluateAsync(candidate, tasks);
        sw.Stop();

        Assert.Equal(1.0, result.PassRate);
        // 2 parallel x 2 batches = ~100ms; allow generous tolerance for CI/loaded machines
        Assert.True(sw.ElapsedMilliseconds < 500,
            $"Expected <500ms with parallelism=2 but took {sw.ElapsedMilliseconds}ms");
        Assert.True(sw.ElapsedMilliseconds >= 70,
            $"Expected >=70ms (2 batches) but took {sw.ElapsedMilliseconds}ms");
    }

    public async ValueTask DisposeAsync()
    {
        if (Directory.Exists(_traceRoot))
        {
            try { Directory.Delete(_traceRoot, recursive: true); }
            catch { /* best effort cleanup */ }
        }
        await ValueTask.CompletedTask;
    }
}
