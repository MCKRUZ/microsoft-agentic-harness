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
            NullLogger<PlanRunExecutor>.Instance);
    }

    private static PlanRunRequest Request(
        CapabilityEnvelope? envelope = null, string agentId = "caller-agent", string? conversationId = null) => new()
    {
        PlanId = PlanId.New(),
        Envelope = envelope ?? new CapabilityEnvelope
        {
            AllowedTools = ["file_system"],
            AutonomyCeiling = AutonomyLevel.Autonomous
        },
        AgentId = agentId,
        ConversationId = conversationId
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

    /// <summary>Shared observation channel between the test and the scoped fake executor.</summary>
    private sealed class RunCapture
    {
        public int Invocations;
        public CapabilityEnvelope? EnvelopeDuringRun;
        public string? AgentIdDuringRun;
        public string? ConversationIdDuringRun;
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
