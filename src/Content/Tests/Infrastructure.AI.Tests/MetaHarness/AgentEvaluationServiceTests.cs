using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.MetaHarness;
using Application.AI.Common.Interfaces.Traces;
using Domain.AI.Agents;
using Domain.Common.Config;
using Domain.Common.Config.MetaHarness;
using Domain.Common.MetaHarness;
using Infrastructure.AI.MetaHarness;
using Infrastructure.AI.Security;
using Infrastructure.AI.Tests.Helpers;
using Infrastructure.AI.Tests.Planner.StepExecutors;
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
            PermissiveAdmission.Pipeline(), PermissiveAdmission.PermissiveSanitizer(),
            NullLoggerFactory.Instance, NullLogger<AgentEvaluationService>.Instance);
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

    /// <summary>
    /// CI's correctness-review finding: <c>_admissionPipeline</c> is a single scoped instance shared
    /// across every eval task and candidate run in this scope, but it was armed via
    /// <c>ToolAdmissionAccessor.Begin</c> without ever being reset first — unlike
    /// <c>DirectToolInvoker.ArmGovernance</c>, which resets before every arm. Without a reset, loop-
    /// detection and call-once state from one task would leak into the next task run in the same scope.
    /// This proves <c>Reset()</c> is called once per task.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_MultipleTasks_ResetsAdmissionPipelineBeforeEachTask()
    {
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestableAIAgent("The answer is 42"));

        var admissionPipeline = new Mock<IToolCallAdmissionPipeline>();
        var cfg = new MetaHarnessConfig { TraceDirectoryRoot = _traceRoot };
        var opts = Mock.Of<IOptionsMonitor<MetaHarnessConfig>>(m => m.CurrentValue == cfg);
        var sut = new AgentEvaluationService(
            opts, BuildTraceStore(cfg.TraceDirectoryRoot), _agentFactoryMock.Object,
            admissionPipeline.Object, PermissiveAdmission.PermissiveSanitizer(),
            NullLoggerFactory.Instance, NullLogger<AgentEvaluationService>.Instance);

        var candidate = BuildCandidate();
        var tasks = new[]
        {
            BuildTask("t1", "question 1", pattern: "answer"),
            BuildTask("t2", "question 2", pattern: "42")
        };

        await sut.EvaluateAsync(candidate, tasks);

        admissionPipeline.Verify(p => p.Reset(), Times.Exactly(tasks.Length));
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

        // #618: keys are relative to the active agent's OWN skill directory (matching the real
        // production capture path, ActiveConfigSnapshotBuilder.EnumerateSkillFilesAsync, which uses
        // Path.GetRelativePath(skillDirectory, filePath) against that ONE skill's own root) - no
        // skill-name-prefixed subdirectory. The previous fixture's "research-agent/SKILL.md" key
        // never matched what production actually captures.
        var skillFiles = new Dictionary<string, string>
        {
            ["SKILL.md"] =
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
    /// #618: the wiring test above only proves an <see cref="AgentSkillsProvider"/> object exists in
    /// the context - it never proved the underlying materialized directory would actually be
    /// DISCOVERED by the real SDK, which is exactly how this gap went unnoticed. Verified directly
    /// against the pinned Microsoft.Agents.AI 1.13.0 package (see MaterializeCandidateSkills' own
    /// remarks): AgentFileSkillsSource requires a SKILL.md's declared name to ordinal-equal its own
    /// containing directory's leaf name. This asserts that invariant directly on disk, independent of
    /// the SDK's own (log-only, not exception-based) failure signal.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_CandidateWithSkillSnapshots_MaterializesSkillUnderACorrectlyNamedDirectory()
    {
        // Scoped to the one run-root this test's own EvaluateAsync call creates, not the whole shared
        // %TEMP%\harness-eval-skills tree - an unscoped SearchOption.AllDirectories scan there would
        // both be an unbounded-cost walk of every past run's leftovers and risk a false pass if a
        // concurrently-running test (e.g. the wiring test above, which uses the same skill name)
        // leaves a same-named directory behind (caught in review).
        var evalSkillsRoot = Path.Combine(Path.GetTempPath(), "harness-eval-skills");
        var preExistingRunRoots = Directory.Exists(evalSkillsRoot)
            ? Directory.EnumerateDirectories(evalSkillsRoot).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

        string? capturedSkillDirectory = null;
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .Callback<AgentExecutionContext, CancellationToken>((ctx, _) =>
            {
                // Read the materialized directory back off disk rather than reflecting into the SDK's
                // provider internals - what matters is proving a "research-agent"-named directory
                // containing SKILL.md exists somewhere under this test's OWN run root while the agent
                // is still being constructed (before EvaluateAsync's finally block deletes it).
                var thisRunRoot = Directory.Exists(evalSkillsRoot)
                    ? Directory.EnumerateDirectories(evalSkillsRoot)
                        .FirstOrDefault(d => !preExistingRunRoots.Contains(d))
                    : null;
                capturedSkillDirectory = thisRunRoot is null
                    ? null
                    : Directory.EnumerateDirectories(thisRunRoot, "*", SearchOption.AllDirectories)
                        .FirstOrDefault(d => Path.GetFileName(d) == "research-agent" && File.Exists(Path.Combine(d, "SKILL.md")));
            })
            .ReturnsAsync(new TestableAIAgent("output"));

        var skillFiles = new Dictionary<string, string>
        {
            ["SKILL.md"] =
                "---\nname: research-agent\ndescription: Finds and analyzes information.\n---\n# Research Agent\nDo research.\n"
        };
        var sut = BuildSut();
        var candidate = BuildCandidate(skillFiles: skillFiles);
        var tasks = new[] { BuildTask("materialize-task", "prompt", pattern: null) };

        await sut.EvaluateAsync(candidate, tasks);

        Assert.NotNull(capturedSkillDirectory);
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
    /// #618: a candidate with skill files but no SKILL.md ANYWHERE in the snapshot (neither a bare
    /// top-level key nor one nested under a skill-name subdirectory) cannot be materialized into a
    /// loadable directory at all. This must fail the task rather than silently degrade to no skills
    /// provider - a silent degrade would score this malformed proposal identically to its unchanged
    /// parent, the same silent-no-op symptom #618 fixes, just reintroduced via a different
    /// malformed-input shape (caught in review).
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_SkillSnapshotsWithNoSkillMdAnywhere_FailsTask()
    {
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestableAIAgent("output"));

        var skillFiles = new Dictionary<string, string> { ["resources/notes.md"] = "some notes" };
        var sut = BuildSut();
        var candidate = BuildCandidate(skillFiles: skillFiles);
        var tasks = new[] { BuildTask("no-skillmd-task", "prompt", pattern: null) };

        var result = await sut.EvaluateAsync(candidate, tasks);

        var taskResult = Assert.Single(result.PerExampleResults);
        Assert.False(taskResult.Passed);
        Assert.Contains("no SKILL.md anywhere in the snapshot", taskResult.FailureReason);
    }

    /// <summary>
    /// #618 (regression caught by CI review): a snapshot captured from a MULTI-skill root has every
    /// key already prefixed with its own skill's directory name (e.g. "research-agent/SKILL.md") -
    /// that shape already satisfies the pinned SDK's naming convention exactly as captured, and must
    /// be materialized as-is, NOT re-nested under an additional derived subdirectory. An earlier
    /// version of this fix unconditionally assumed every snapshot was the bare top-level SKILL.md
    /// shape and threw for this one instead, breaking a layout that materialized and loaded
    /// correctly before #618's fix ever existed.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_MultiSkillSnapshotWithPrefixedKeys_MaterializesAsIsWithoutRenesting()
    {
        var evalSkillsRoot = Path.Combine(Path.GetTempPath(), "harness-eval-skills");
        var preExistingRunRoots = Directory.Exists(evalSkillsRoot)
            ? Directory.EnumerateDirectories(evalSkillsRoot).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

        AgentExecutionContext? capturedContext = null;
        // Checked INSIDE the callback, before EvaluateAsync's own finally block deletes the temp
        // directory - the caller never gets the path back, so this is the only window it exists in.
        bool? researchAgentSkillMdExistedAtTheRightPath = null;
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .Callback<AgentExecutionContext, CancellationToken>((ctx, _) =>
            {
                capturedContext = ctx;
                var thisRunRoot = Directory.Exists(evalSkillsRoot)
                    ? Directory.EnumerateDirectories(evalSkillsRoot)
                        .FirstOrDefault(d => !preExistingRunRoots.Contains(d))
                    : null;
                // Must land exactly one level under the run root (not re-nested a second time under
                // an additional derived-name subdirectory).
                researchAgentSkillMdExistedAtTheRightPath = thisRunRoot is not null
                    && File.Exists(Path.Combine(thisRunRoot, "research-agent", "SKILL.md"));
            })
            .ReturnsAsync(new TestableAIAgent("output"));

        var skillFiles = new Dictionary<string, string>
        {
            ["research-agent/SKILL.md"] =
                "---\nname: research-agent\ndescription: Finds and analyzes information.\n---\nDo research.\n",
            ["other-skill/SKILL.md"] =
                "---\nname: other-skill\ndescription: Does something else.\n---\nDo other things.\n"
        };
        var sut = BuildSut();
        var candidate = BuildCandidate(skillFiles: skillFiles);
        var tasks = new[] { BuildTask("multi-skill-task", "prompt", pattern: null) };

        var result = await sut.EvaluateAsync(candidate, tasks);

        var taskResult = Assert.Single(result.PerExampleResults);
        Assert.True(taskResult.Passed);
        Assert.NotNull(capturedContext);
        Assert.NotNull(capturedContext.AIContextProviders);
        Assert.Single(capturedContext.AIContextProviders!.OfType<AgentSkillsProvider>());
        Assert.True(researchAgentSkillMdExistedAtTheRightPath);
    }

    /// <summary>
    /// #618 (2nd CI-caught regression, hybrid shape): both shapes can coexist in ONE snapshot -
    /// ProposeChangesExecutor.ApplyProposalToSnapshot merges an LLM-authored proposal's keys into the
    /// current snapshot with no shape validation, so a proposal against an already-multi-skill seed
    /// can add a bare top-level SKILL.md alongside pre-existing "research-agent/SKILL.md"-shaped
    /// entries. Classifying the WHOLE snapshot from one key (the previous version of this fix)
    /// mis-routes the already-correct "other-skill" entry into the bare skill's derived-name
    /// subdirectory whenever a bare key is also present. Both groups must land independently and
    /// correctly in the same materialization.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_HybridSnapshotWithBareAndPrefixedKeys_PlacesEachGroupIndependently()
    {
        var evalSkillsRoot = Path.Combine(Path.GetTempPath(), "harness-eval-skills");
        var preExistingRunRoots = Directory.Exists(evalSkillsRoot)
            ? Directory.EnumerateDirectories(evalSkillsRoot).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

        bool? bareSkillLandedUnderItsDerivedName = null;
        bool? prefixedSkillLandedAtItsOwnUnmodifiedPath = null;
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .Callback<AgentExecutionContext, CancellationToken>((_, _) =>
            {
                var thisRunRoot = Directory.Exists(evalSkillsRoot)
                    ? Directory.EnumerateDirectories(evalSkillsRoot)
                        .FirstOrDefault(d => !preExistingRunRoots.Contains(d))
                    : null;
                bareSkillLandedUnderItsDerivedName = thisRunRoot is not null
                    && File.Exists(Path.Combine(thisRunRoot, "bare-skill", "SKILL.md"));
                // Must NOT have been dragged one level deeper under "bare-skill/other-skill/..." -
                // it belongs at the run root, exactly as its own key says.
                prefixedSkillLandedAtItsOwnUnmodifiedPath = thisRunRoot is not null
                    && File.Exists(Path.Combine(thisRunRoot, "other-skill", "SKILL.md"))
                    && !File.Exists(Path.Combine(thisRunRoot, "bare-skill", "other-skill", "SKILL.md"));
            })
            .ReturnsAsync(new TestableAIAgent("output"));

        var skillFiles = new Dictionary<string, string>
        {
            ["SKILL.md"] =
                "---\nname: bare-skill\ndescription: Captured from its own directory directly.\n---\nbody",
            ["other-skill/SKILL.md"] =
                "---\nname: other-skill\ndescription: Already correctly shaped.\n---\nbody"
        };
        var sut = BuildSut();
        var candidate = BuildCandidate(skillFiles: skillFiles);
        var tasks = new[] { BuildTask("hybrid-task", "prompt", pattern: null) };

        var result = await sut.EvaluateAsync(candidate, tasks);

        var taskResult = Assert.Single(result.PerExampleResults);
        Assert.True(taskResult.Passed);
        Assert.True(bareSkillLandedUnderItsDerivedName);
        Assert.True(prefixedSkillLandedAtItsOwnUnmodifiedPath);
    }

    /// <summary>
    /// #618 (3rd CI/review-caught regression): a key having a directory segment does NOT by itself
    /// mean it belongs to an already-correct sibling skill. A bare-rooted skill's OWN resource files
    /// (a normal skill-authoring convention - SkillResource.RelativePath documents resources as
    /// relative to "the skill's base directory") also have a directory segment, e.g.
    /// "resources/notes.md" alongside a bare top-level SKILL.md. Classifying by "has a slash" alone
    /// (the previous version of this fix) routes that resource file to the run root as a sibling of
    /// the skill's derived-name subdirectory instead of inside it, severing it from its own skill -
    /// the same silent-degradation failure class this whole PR chain exists to close.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_BareSkillWithItsOwnResourceSubfolder_NestsTheResourceUnderTheSameDerivedName()
    {
        var evalSkillsRoot = Path.Combine(Path.GetTempPath(), "harness-eval-skills");
        var preExistingRunRoots = Directory.Exists(evalSkillsRoot)
            ? Directory.EnumerateDirectories(evalSkillsRoot).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

        bool? resourceLandedInsideTheSkillDirectory = null;
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .Callback<AgentExecutionContext, CancellationToken>((_, _) =>
            {
                var thisRunRoot = Directory.Exists(evalSkillsRoot)
                    ? Directory.EnumerateDirectories(evalSkillsRoot)
                        .FirstOrDefault(d => !preExistingRunRoots.Contains(d))
                    : null;
                resourceLandedInsideTheSkillDirectory = thisRunRoot is not null
                    && File.Exists(Path.Combine(thisRunRoot, "research-agent", "resources", "notes.md"));
            })
            .ReturnsAsync(new TestableAIAgent("output"));

        var skillFiles = new Dictionary<string, string>
        {
            ["SKILL.md"] =
                "---\nname: research-agent\ndescription: Finds and analyzes information.\n---\nDo research.\n",
            ["resources/notes.md"] = "some research notes"
        };
        var sut = BuildSut();
        var candidate = BuildCandidate(skillFiles: skillFiles);
        var tasks = new[] { BuildTask("bare-with-resources-task", "prompt", pattern: null) };

        var result = await sut.EvaluateAsync(candidate, tasks);

        var taskResult = Assert.Single(result.PerExampleResults);
        Assert.True(taskResult.Passed);
        Assert.True(resourceLandedInsideTheSkillDirectory);
    }

    /// <summary>
    /// #618: a SKILL.md with no 'name' in its frontmatter has nothing to derive the required
    /// correctly-named subdirectory from. This must fail the task rather than silently degrade to no
    /// skills provider, for the same reason as the missing-SKILL.md case above.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_SkillMdWithNoDeclaredName_FailsTask()
    {
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestableAIAgent("output"));

        var skillFiles = new Dictionary<string, string>
        {
            ["SKILL.md"] = "---\ndescription: Missing a name field.\n---\nbody"
        };
        var sut = BuildSut();
        var candidate = BuildCandidate(skillFiles: skillFiles);
        var tasks = new[] { BuildTask("no-name-task", "prompt", pattern: null) };

        var result = await sut.EvaluateAsync(candidate, tasks);

        var taskResult = Assert.Single(result.PerExampleResults);
        Assert.False(taskResult.Passed);
        Assert.Contains("declares no 'name'", taskResult.FailureReason);
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

        // #618: a valid top-level SKILL.md is required to reach the per-file materialization loop at
        // all (see MaterializeCandidateSkills' own remarks) - a traversal key with no valid SKILL.md
        // entry would be rejected earlier, for a different reason, proving nothing about THIS
        // protection. The traversal attempt lives in a second, non-SKILL.md entry instead.
        var skillFiles = new Dictionary<string, string>
        {
            ["SKILL.md"] = "---\nname: evil\ndescription: escape attempt.\n---\nbody",
            ["../escaped/resource.md"] = "malicious resource content"
        };
        var sut = BuildSut();
        var candidate = BuildCandidate(skillFiles: skillFiles);
        var tasks = new[] { BuildTask("traversal-task", "prompt", pattern: null) };

        var result = await sut.EvaluateAsync(candidate, tasks);

        var taskResult = Assert.Single(result.PerExampleResults);
        Assert.False(taskResult.Passed);
        Assert.Contains("resolves outside", taskResult.FailureReason);
    }

    /// <summary>
    /// #618: the skill NAME itself is untrusted, candidate-authored input with exactly the same
    /// traversal risk as any snapshot file key - SafeResolveWithinRoot is reused for it rather than a
    /// new, unverified sanitizer, so this proves that reuse actually rejects a malicious name.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_SkillSnapshotWithPathTraversalName_FailsTaskAndDoesNotEscape()
    {
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestableAIAgent("output"));

        var skillFiles = new Dictionary<string, string>
        {
            ["SKILL.md"] = "---\nname: \"../../escaped\"\ndescription: escape attempt via name.\n---\nbody"
        };
        var sut = BuildSut();
        var candidate = BuildCandidate(skillFiles: skillFiles);
        var tasks = new[] { BuildTask("name-traversal-task", "prompt", pattern: null) };

        var result = await sut.EvaluateAsync(candidate, tasks);

        var taskResult = Assert.Single(result.PerExampleResults);
        Assert.False(taskResult.Passed);
        Assert.Contains("resolves outside", taskResult.FailureReason);
    }

    // ── Token cost: the second thing evaluation reports (issue #267) ──────────

    /// <summary>
    /// The provider's own billed figure is what a cost comparison should use, so it must reach the result.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_ProviderReportsUsage_ReportsThatCostRatherThanZero()
    {
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestableAIAgent(_ => new AgentResponse(
                new ChatMessage(ChatRole.Assistant, "the answer is 42"))
            {
                Usage = new UsageDetails { InputTokenCount = 900, OutputTokenCount = 100, TotalTokenCount = 1000 }
            }));

        var sut = BuildSut();
        var tasks = new[] { BuildTask("t1", "question 1"), BuildTask("t2", "question 2") };

        var result = await sut.EvaluateAsync(BuildCandidate(), tasks);

        // Cost feeds candidate selection. Pinned to zero, an agent that burned three times the context
        // scored as identically cheap to one that did not, so the cheaper agent could never win.
        Assert.Equal(2000L, result.TotalTokenCost);
        Assert.All(result.PerExampleResults, r => Assert.Equal(1000L, r.TokenCost));
    }

    /// <summary>
    /// Providers that report no usage — the echo client used offline among them — must still yield a
    /// comparable figure rather than falling back to the zero this issue exists to remove.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_ProviderReportsNoUsage_EstimatesRatherThanReportingZero()
    {
        const string prompt = "a question long enough to estimate above zero tokens";
        const string answer = "an answer long enough to estimate above zero tokens";

        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestableAIAgent(answer));

        var sut = BuildSut();
        var result = await sut.EvaluateAsync(BuildCandidate(), [BuildTask("t1", prompt)]);

        var expected = TokenEstimationHelper.EstimateTokens(prompt)
            + TokenEstimationHelper.EstimateTokens(answer);

        // Control on the control: an estimate that happened to be zero would make this assertion vacuous.
        Assert.True(expected > 0);
        Assert.Equal(expected, Assert.Single(result.PerExampleResults).TokenCost);
    }

    /// <summary>
    /// A task that threw has no response to read usage from, and the throw may have come before anything
    /// reached the provider or after a full turn was billed. Zero is recorded as a genuine unknown, and
    /// this pins that as a decision rather than leaving it to look like the defect that was just fixed.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_TaskThrew_ReportsZeroBecauseTheCostIsUnknowable()
    {
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestableAIAgent.Throwing(new InvalidOperationException("provider exploded")));

        var sut = BuildSut();
        var result = await sut.EvaluateAsync(BuildCandidate(), [BuildTask("t1", "question")]);

        var taskResult = Assert.Single(result.PerExampleResults);
        Assert.False(taskResult.Passed);
        Assert.Equal(0L, taskResult.TokenCost);
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
