using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Services.Agent;
using Application.AI.Common.Services.Governance;
using Domain.AI.Bundles;
using Domain.AI.Governance;
using Domain.AI.Planner;
using Domain.Common;
using Infrastructure.AI.Planner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Planner;

/// <summary>
/// Verifies the single arming site for enveloped plan runs: <see cref="PlanRunExecutor"/> must
/// initialize the scoped governance identity and publish the capability envelope for exactly the
/// duration of the run, fail closed on requests that cannot express a governed run, and never leak
/// raw exception text into results.
/// </summary>
public sealed class PlanRunExecutorTests
{
    private readonly RunCapture _capture = new();
    private readonly Mock<IConversationBudgetTracker> _budget = new();
    private readonly PlanRunExecutor _sut;

    public PlanRunExecutorTests()
    {
        // Real scoped AgentExecutionContext + a capturing IPlanExecutor resolved from the same
        // scope, so the test observes exactly what production step executors would observe.
        var services = new ServiceCollection();
        services.AddSingleton(_capture);
        services.AddScoped<IAgentExecutionContext, AgentExecutionContext>();
        services.AddScoped<IPlanExecutor, CapturingPlanExecutor>();
        var provider = services.BuildServiceProvider();

        _sut = new PlanRunExecutor(
            provider.GetRequiredService<IServiceScopeFactory>(),
            _budget.Object,
            NullLogger<PlanRunExecutor>.Instance);
    }

    private static PlanRunRequest Request(
        CapabilityEnvelope? envelope = null, string agentId = "caller-agent", string? conversationId = null,
        string? runId = null) => new()
    {
        PlanId = PlanId.New(),
        Envelope = envelope ?? new CapabilityEnvelope
        {
            AllowedTools = ["file_system"],
            AutonomyCeiling = AutonomyLevel.Autonomous
        },
        AgentId = agentId,
        ConversationId = conversationId,
        RunId = runId
    };

    [Fact]
    public async Task ExecuteAsync_ValidRequest_ArmsEnvelopeAndIdentityForTheRun()
    {
        var request = Request(conversationId: "conv-1");

        var result = await _sut.ExecuteAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, _capture.Invocations);
        Assert.Same(request.Envelope, _capture.EnvelopeDuringRun);
        Assert.Equal("caller-agent", _capture.AgentIdDuringRun);
        Assert.Equal("conv-1", _capture.ConversationIdDuringRun);
        // The test's own flow never sees the envelope — arming is confined to the run.
        Assert.Null(CapabilityEnvelopeAccessor.Current);
    }

    [Fact]
    public async Task ExecuteAsync_NoConversationId_DefaultsToPlanId()
    {
        var request = Request();

        await _sut.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(request.PlanId.Value.ToString(), _capture.ConversationIdDuringRun);
    }

    [Fact]
    public async Task ExecuteAsync_RunIdSupplied_BecomesTheCallOnceScope()
    {
        var request = Request(conversationId: "conv-1", runId: "run-42");

        await _sut.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal("run-42", _capture.CallOnceScopeIdDuringRun);
    }

    [Fact]
    public async Task ExecuteAsync_NoRunId_CallOnceScopeIsNull_NotDerivedFromConversationOrPlanId()
    {
        // The defect this guards against: a call-once gate reading ConversationId directly would
        // see the plan-id fallback here and treat every run of this workflow as sharing one scope —
        // the first claim under a call-once tool would then permanently refuse every future run,
        // for every caller, with no release short of the retention window. RunId must stay
        // independent of both ConversationId and its plan-id fallback.
        var request = Request();

        await _sut.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(request.PlanId.Value.ToString(), _capture.ConversationIdDuringRun);
        Assert.Null(_capture.CallOnceScopeIdDuringRun);
    }

    [Fact]
    public async Task ExecuteAsync_MissingEnvelope_FailsClosedWithoutExecuting()
    {
        // Built directly (not via Request()) so the envelope is genuinely null.
        var request = new PlanRunRequest
        {
            PlanId = PlanId.New(),
            Envelope = null!,
            AgentId = "caller-agent"
        };

        var result = await _sut.ExecuteAsync(request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("plan_run.envelope_required", result.Errors);
        Assert.Equal(0, _capture.Invocations);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExecuteAsync_MissingAgentIdentity_FailsClosedWithoutExecuting(string agentId)
    {
        var result = await _sut.ExecuteAsync(Request(agentId: agentId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("plan_run.agent_identity_required", result.Errors);
        Assert.Equal(0, _capture.Invocations);
    }

    [Theory]
    [InlineData("agent/../admin")]          // path traversal shape
    [InlineData("agent*")]                   // glob wildcard — permission rules are glob-matched
    [InlineData("agent\nsubject=admin")]     // newline — audit log forging
    [InlineData("agent id")]                 // whitespace
    public async Task ExecuteAsync_MalformedAgentIdentity_FailsClosedWithoutExecuting(string agentId)
    {
        // The id is the permission-resolution key and the audit subject, so it is constrained before
        // it can reach either.
        var result = await _sut.ExecuteAsync(Request(agentId: agentId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("plan_run.agent_identity_invalid", result.Errors);
        Assert.Equal(0, _capture.Invocations);
    }

    [Fact]
    public async Task ExecuteAsync_OverlongAgentIdentity_FailsClosedWithoutExecuting()
    {
        var result = await _sut.ExecuteAsync(
            Request(agentId: new string('a', PlanRunRequest.MaxAgentIdLength + 1)), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("plan_run.agent_identity_invalid", result.Errors);
        Assert.Equal(0, _capture.Invocations);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("conv id")]
    [InlineData("conv*")]
    public async Task ExecuteAsync_MalformedConversationId_FailsClosedWithoutExecuting(string conversationId)
    {
        // A blank conversation id would yield a blank run scope, which the step executor's
        // IsNullOrEmpty check reads as "no run scope" — silently disabling the run-level budget gate
        // while the execution context stayed bound to an empty conversation.
        var result = await _sut.ExecuteAsync(
            Request(conversationId: conversationId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("plan_run.conversation_id_invalid", result.Errors);
        Assert.Equal(0, _capture.Invocations);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("run id")]
    [InlineData("run*")]
    // /code-review finding: these clear IsWellFormedAgentId's charset entirely — "." / ".." / a
    // trailing dot are all directory-traversal or Windows-dot-stripping shapes the charset alone
    // cannot exclude. See PlanRunExecutor's own remarks on this check for the full rationale. "C:" is
    // covered separately below — its rejection is Windows-specific, not universal (build-and-test
    // finding; see that test's own remarks).
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("run-id.")]
    public async Task ExecuteAsync_MalformedRunId_FailsClosedWithoutExecuting(string runId)
    {
        // #560: RunId becomes CallOnceScopeId, which is exactly what ToolResultScopeId resolves to —
        // the same directory-name role ConversationId's own check above already guards. Mirrors
        // ExecuteAsync_MalformedConversationId_FailsClosedWithoutExecuting for the sibling field.
        var result = await _sut.ExecuteAsync(Request(runId: runId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("plan_run.run_id_invalid", result.Errors);
        Assert.Equal(0, _capture.Invocations);
    }

    [Fact]
    public async Task ExecuteAsync_WindowsDriveRootedRunId_FailsClosedOnlyOnWindows()
    {
        // Build-and-test finding: Path.IsPathRooted("C:") is true (drive-rooted) only on Windows —
        // StorageSegmentSafety.HasUnsafeShape correctly measures it as NOT rooted on Linux/macOS,
        // where drive letters do not exist and the allowed charset already excludes the only character
        // ('/') that IS rooted there. See FileSystemToolResultStoreTests' identical-shaped test for the
        // same reasoning applied to the sibling sessionId check.
        var result = await _sut.ExecuteAsync(Request(runId: "C:"), CancellationToken.None);

        if (OperatingSystem.IsWindows())
        {
            Assert.False(result.IsSuccess);
            Assert.Contains("plan_run.run_id_invalid", result.Errors);
            Assert.Equal(0, _capture.Invocations);
        }
        else
        {
            Assert.True(result.IsSuccess);
        }
    }

    [Fact]
    public async Task ExecuteAsync_NullConversationId_IsAcceptedAndDerivesScopeFromThePlan()
    {
        // Null is the documented "derive it from the plan id" case, distinct from blank.
        var request = Request();

        var result = await _sut.ExecuteAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(request.PlanId.Value.ToString(), _capture.ConversationIdDuringRun);
    }

    [Theory]
    [InlineData("bundle-agent")]
    [InlineData("tenant:team.agent_1")]
    public async Task ExecuteAsync_WellFormedAgentIdentity_Executes(string agentId)
    {
        var result = await _sut.ExecuteAsync(Request(agentId: agentId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(agentId, _capture.AgentIdDuringRun);
    }

    [Fact]
    public async Task ExecuteAsync_ExecutorThrows_ReturnsScrubbedFailureAndDisarms()
    {
        _capture.ThrowOnExecute = new InvalidOperationException("DataSource=/secret/path;token=abc");

        var result = await _sut.ExecuteAsync(Request(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("plan_run.execution_failed", result.Errors);
        // Raw exception text (which can carry paths/tokens) must never surface in the result.
        Assert.DoesNotContain(result.Errors, e => e.Contains("secret"));
        Assert.Null(CapabilityEnvelopeAccessor.Current);
    }

    [Fact]
    public async Task ExecuteAsync_Cancellation_PropagatesUntouched()
    {
        _capture.ThrowOnExecute = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _sut.ExecuteAsync(Request(), CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_ReleasesTheRunOwnedBudgetKey_OnEveryExitPath(bool throws)
    {
        // No conversation handler owns this key — that is the point of it — so the run must free it
        // itself, including when execution throws.
        var request = Request(conversationId: "conv-1");
        if (throws)
            _capture.ThrowOnExecute = new InvalidOperationException("boom");

        await _sut.ExecuteAsync(request, CancellationToken.None);

        _budget.Verify(
            b => b.ReleaseAsync(
                PlanRunKeys.RunBudgetKey("conv-1"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>Shared observation channel between the test and the scoped fake executor.</summary>
    private sealed class RunCapture
    {
        public int Invocations;
        public CapabilityEnvelope? EnvelopeDuringRun;
        public string? AgentIdDuringRun;
        public string? ConversationIdDuringRun;
        public string? CallOnceScopeIdDuringRun;
        public Exception? ThrowOnExecute;
    }

    /// <summary>
    /// Fake plan executor that records the ambient envelope and the scoped identity exactly as a
    /// production step executor would see them mid-run.
    /// </summary>
    private sealed class CapturingPlanExecutor(IAgentExecutionContext agentContext, RunCapture capture) : IPlanExecutor
    {
        public Task<Result<PlanExecutionSummary>> ExecuteAsync(PlanId planId, CancellationToken ct)
        {
            capture.Invocations++;
            capture.EnvelopeDuringRun = CapabilityEnvelopeAccessor.Current;
            capture.AgentIdDuringRun = agentContext.AgentId;
            capture.ConversationIdDuringRun = agentContext.ConversationId;
            capture.CallOnceScopeIdDuringRun = agentContext.CallOnceScopeId;

            if (capture.ThrowOnExecute is not null)
                throw capture.ThrowOnExecute;

            return Task.FromResult(Result<PlanExecutionSummary>.Success(new PlanExecutionSummary
            {
                PlanId = planId,
                FinalStatus = StepExecutionStatus.Completed,
                TotalDuration = TimeSpan.Zero,
                StepStates = []
            }));
        }

        public Task<Result<PlanExecutionSummary>> ExecuteAsync(PlanId planId, PlanExecutionContext context, CancellationToken ct)
            => ExecuteAsync(planId, ct);

        public Task<Result> CancelAsync(PlanId planId, CancellationToken ct)
            => Task.FromResult(Result.Success());

        public Task<Result> RetryStepAsync(PlanId planId, PlanStepId stepId, CancellationToken ct)
            => Task.FromResult(Result.Success());
    }
}
