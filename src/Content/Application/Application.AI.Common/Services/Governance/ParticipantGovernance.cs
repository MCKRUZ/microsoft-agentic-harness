using System.Runtime.CompilerServices;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// Wraps an agent that an orchestration engine runs on its own schedule — a Magentic participant — so
/// each run is governed as <em>that agent</em>: a fresh child scope, armed with the agent's own id,
/// whose admission pipeline is the one ambient for every tool call the run makes.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a wrapper.</strong> A delegation has one call site of ours to arm around
/// (<c>CapabilityMatchSupervisor.ExecuteAgent</c>). Magentic hands the whole workflow to Microsoft's
/// engine, which decides when each participant runs, so the only place left is the participant's own
/// <c>RunAsync</c>/<c>RunStreamingAsync</c>. It is attached through the framework's
/// <see cref="AIAgentBuilder"/> middleware seam (as <c>AgentFactory</c> does), so every member of the agent
/// still passes through to the real one. Only <c>MagenticAgentTurnRunner</c> applies it: a caller that
/// drives the orchestrator directly gets no participant governance.
/// </para>
/// <para>
/// <strong>Armed as the participant, inside the supervisor's session.</strong> Per-agent permission rules
/// and the denial rate-limiter see the participant's id. Conversation id, call-once scope and workload
/// identity come from the supervisor's turn (<see cref="GovernanceArmingPolicy.Delegation"/>), so a
/// call-once tool the supervisor claimed stays claimed. Each run opens a new scope.
/// </para>
/// <para>
/// <strong>Streaming publishes the pipeline around every step.</strong> Ambient (<c>AsyncLocal</c>)
/// state set inside an async iterator does not survive a <c>yield</c>: the consumer resumes the iterator
/// under its own context. A pipeline published once would govern only the first step, so each step is
/// awaited inside <see cref="ToolAdmissionAccessor.Begin"/> and released before the update is handed on.
/// </para>
/// <para>
/// <strong>The participant's trace is folded</strong> into the supervisor turn's trace when each run ends,
/// including one that throws: the child scope's recorder is gone with the scope, and the turn result
/// reports the parent's.
/// </para>
/// <para>
/// <strong>Known limits.</strong> Telemetry attribution on a stream is published once, in the first step,
/// so later steps can carry the supervisor's (#803). Per-run state (the loop guard's history, the aggregate
/// output budget) starts empty every run (#804).
/// </para>
/// </remarks>
public sealed class ParticipantGovernance
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAgentExecutionContext _parentContext;
    private readonly IGovernanceTraceRecorder _parentTrace;

    /// <summary>
    /// Initializes a new instance of the <see cref="ParticipantGovernance"/> class. Scoped: it binds to the
    /// supervisor turn's own execution context and trace recorder.
    /// </summary>
    /// <param name="scopeFactory">Opens the child scope each run is armed in.</param>
    /// <param name="parentContext">The supervisor turn's execution context, which participants inherit from.</param>
    /// <param name="parentTrace">The supervisor turn's trace recorder, which participants' traces fold into.</param>
    public ParticipantGovernance(
        IServiceScopeFactory scopeFactory,
        IAgentExecutionContext parentContext,
        IGovernanceTraceRecorder parentTrace)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(parentContext);
        ArgumentNullException.ThrowIfNull(parentTrace);

        _scopeFactory = scopeFactory;
        _parentContext = parentContext;
        _parentTrace = parentTrace;
    }

    /// <summary>Returns <paramref name="agent"/> wrapped so every run is governed as <paramref name="agentId"/>.</summary>
    /// <param name="agent">The participant.</param>
    /// <param name="agentId">The participant's own agent id: the identity its tool calls are authorized as.</param>
    /// <param name="fallbackScopeId">
    /// Conversation id and call-once scope to use where the parent supplies none; the supervisor's
    /// conversation id.
    /// </param>
    public AIAgent Wrap(AIAgent agent, string agentId, string fallbackScopeId)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrEmpty(fallbackScopeId);

        return agent.AsBuilder()
            .Use(
                runFunc: (messages, session, options, inner, ct) =>
                    RunAsync(agentId, fallbackScopeId, messages, session, options, inner, ct),
                runStreamingFunc: (messages, session, options, inner, ct) =>
                    RunStreamingAsync(agentId, fallbackScopeId, messages, session, options, inner, ct))
            .Build();
    }

    private async Task<AgentResponse> RunAsync(
        string agentId, string fallbackScopeId, IEnumerable<ChatMessage> messages, AgentSession? session,
        AgentRunOptions? options, AIAgent inner, CancellationToken cancellationToken)
    {
        await using var run = Arm(agentId, fallbackScopeId);

        using (ToolAdmissionAccessor.Begin(run.Pipeline))
            return await inner.RunAsync(messages, session, options, cancellationToken).ConfigureAwait(false);
    }

    private async IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(
        string agentId, string fallbackScopeId, IEnumerable<ChatMessage> messages, AgentSession? session,
        AgentRunOptions? options, AIAgent inner, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Declared in this order so the stream is disposed first and the run folds its trace afterwards: a
        // throwing dispose cannot strand the trace, and the fold sees everything the stream did.
        await using var run = Arm(agentId, fallbackScopeId);
        await using var stream = inner.RunStreamingAsync(messages, session, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            // Set and released within one segment of this iterator: in force while the inner agent runs
            // (its tool calls happen inside MoveNextAsync), gone before the update reaches the consumer.
            bool hasNext;
            using (ToolAdmissionAccessor.Begin(run.Pipeline))
                hasNext = await stream.MoveNextAsync().ConfigureAwait(false);

            if (!hasNext)
                yield break;

            yield return stream.Current;
        }
    }

    // The same open/arm/dispose-on-throw sequence as CapabilityMatchSupervisor.ArmDelegationGovernance —
    // keep the two in step until a shared governed-child-scope handle replaces both.
    private ArmedRun Arm(string agentId, string fallbackScopeId)
    {
        var scope = _scopeFactory.CreateAsyncScope();
        try
        {
            var pipeline = GovernanceArmer.ArmWithAdmission(
                scope.ServiceProvider,
                agentId,
                GovernanceArmingPolicy.Delegation,
                _parentContext,
                fallbackScopeId);

            return new ArmedRun(scope, pipeline, _parentTrace);
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    /// <summary>One run's scope and pipeline; disposing it folds the run's trace into the parent's, then the scope.</summary>
    private sealed class ArmedRun(
        AsyncServiceScope scope, IToolCallAdmissionPipeline pipeline, IGovernanceTraceRecorder parentTrace)
        : IAsyncDisposable
    {
        public IToolCallAdmissionPipeline Pipeline => pipeline;

        public async ValueTask DisposeAsync()
        {
            try
            {
                parentTrace.Absorb(pipeline.GetTrace());
            }
            finally
            {
                await scope.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
