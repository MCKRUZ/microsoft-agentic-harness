using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.MetaHarness;
using Application.AI.Common.Interfaces.Traces;
using Domain.AI.Agents;
using Domain.Common.Config;
using Domain.Common.Config.MetaHarness;
using Domain.Common.MetaHarness;
using Infrastructure.AI.MetaHarness;
using Infrastructure.AI.Security;
using Infrastructure.AI.Tests.Helpers;
using Infrastructure.AI.Traces;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
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
    /// With MaxEvalParallelism=2 and 4 tasks, exactly two tasks must be in flight at the
    /// peak. Asserted via an observed-concurrency counter rather than wall-clock elapsed
    /// time: a timing ceiling flakes on loaded CI runners (it was seen failing at ~505ms
    /// against a 500ms bound). The counter tests the real property — concurrency level —
    /// deterministically: peak == 2 proves it is neither sequential (>=2) nor uncapped (&lt;=2).
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_WithParallelism2_RunsTasksConcurrently()
    {
        var current = 0;
        var peak = 0;
        var peakLock = new object();

        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new TestableAIAgent(async (_, ct) =>
            {
                var inFlight = Interlocked.Increment(ref current);
                lock (peakLock) { peak = Math.Max(peak, inFlight); }
                try
                {
                    // The delay widens the overlap window so two tasks are genuinely
                    // concurrent; the assertion is on the counter, not on the clock.
                    await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
                    return new AgentResponse(new ChatMessage(ChatRole.Assistant, "ok"));
                }
                finally
                {
                    Interlocked.Decrement(ref current);
                }
            }));

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

        var result = await sut.EvaluateAsync(candidate, tasks);

        Assert.Equal(1.0, result.PassRate);
        Assert.Equal(2, peak);
    }

    /// <summary>
    /// A candidate's proposed skill content must actually reach the eval agent, otherwise
    /// skill-only proposals would grade identically to their parent (a silent no-op).
    /// The eval context must therefore carry a MAF <see cref="AgentSkillsProvider"/> sourced
    /// from the candidate's snapshot.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_CandidateWithSkillSnapshots_WiresSkillsProviderIntoEvalContext()
    {
        AgentExecutionContext? capturedContext = null;
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .Callback<AgentExecutionContext, CancellationToken>((ctx, _) => capturedContext = ctx)
            .ReturnsAsync(new TestableAIAgent("output"));

        var skillFiles = new Dictionary<string, string>
        {
            ["research-agent/SKILL.md"] =
                "---\nname: research-agent\ndescription: Finds and analyzes information.\n---\n# Research Agent\nDo research.\n"
        };
        var sut = BuildSut();
        var candidate = BuildCandidate(skillFiles: skillFiles);
        var tasks = new[] { BuildTask("provider-task", "prompt", pattern: null) };

        await sut.EvaluateAsync(candidate, tasks);

        Assert.NotNull(capturedContext);
        Assert.NotNull(capturedContext.AIContextProviders);
        Assert.Single(capturedContext.AIContextProviders!.OfType<AgentSkillsProvider>());
    }

    /// <summary>
    /// A candidate with no skill snapshots must not wire an empty skills provider, and must
    /// not leave a materialized temp directory behind.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_EmptySkillSnapshots_DoesNotWireSkillsProvider()
    {
        AgentExecutionContext? capturedContext = null;
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .Callback<AgentExecutionContext, CancellationToken>((ctx, _) => capturedContext = ctx)
            .ReturnsAsync(new TestableAIAgent("output"));

        var sut = BuildSut();
        var candidate = BuildCandidate(); // empty SkillFileSnapshots
        var tasks = new[] { BuildTask("empty-task", "prompt", pattern: null) };

        await sut.EvaluateAsync(candidate, tasks);

        Assert.NotNull(capturedContext);
        Assert.True(
            capturedContext.AIContextProviders is null
            || !capturedContext.AIContextProviders.OfType<AgentSkillsProvider>().Any());
    }

    /// <summary>
    /// Snapshot keys come from untrusted LLM proposals, so a path-traversal key must be rejected
    /// (graded as a failed task) and must never write outside the eval temp root.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_SkillSnapshotWithPathTraversalKey_FailsTaskAndDoesNotEscape()
    {
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestableAIAgent("output"));

        var skillFiles = new Dictionary<string, string>
        {
            ["../escaped/SKILL.md"] = "---\nname: evil\ndescription: escape attempt.\n---\nbody"
        };
        var sut = BuildSut();
        var candidate = BuildCandidate(skillFiles: skillFiles);
        var tasks = new[] { BuildTask("traversal-task", "prompt", pattern: null) };

        var result = await sut.EvaluateAsync(candidate, tasks);

        var taskResult = Assert.Single(result.PerExampleResults);
        Assert.False(taskResult.Passed);
        Assert.Contains("resolves outside", taskResult.FailureReason);
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
