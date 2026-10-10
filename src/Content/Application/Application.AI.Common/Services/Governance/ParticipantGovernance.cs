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
/// <strong>Why a wrapper at all.</strong> A delegation has one call site in our code to wrap
/// (<c>CapabilityMatchSupervisor.ExecuteAgent</c>). Magentic hands the whole workflow to Microsoft's
/// engine, which decides internally when the manager and each participant run, so there is no call site
/// of ours around any one of them. The only place left to arm a participant is its own
/// <c>RunAsync</c>/<c>RunStreamingAsync</c>, which this attaches through the framework's own
/// <see cref="AIAgentBuilder"/> middleware seam (the same one <c>AgentFactory</c> uses), so every member
/// of the agent keeps passing through to the real one.
/// </para>
/// <para>
/// <strong>Armed as the participant, inside the supervisor's session.</strong> The agent id is the
/// participant's own, so per-agent permission rules and the denial rate-limiter see the participant and
/// not the entry agent. Conversation id, call-once scope and workload identity are inherited from the
/// supervisor's turn (<see cref="GovernanceArmingPolicy.Delegation"/>), so a call-once tool the
/// supervisor claimed stays claimed for every participant it runs, and the participant is authorized as
/// the real caller. Each run opens a new scope: a participant can run many times in one workflow, and
/// the pipeline's per-run state (loop guard, trace) must not carry from one to the next.
/// </para>
/// <para>
/// <strong>Streaming publishes the pipeline around every step, not once.</strong> Microsoft's engine
/// drives participants through <c>RunStreamingAsync</c>, and ambient (<c>AsyncLocal</c>) state set inside
/// an async iterator does not survive a <c>yield</c>: the code consuming the stream resumes the iterator
/// under its <em>own</em> context. A pipeline published once at the top would therefore govern only the
/// first step, and every later tool call would run under whatever was ambient outside — the
/// unauthorized-as-the-wrong-agent defect this exists to close, reintroduced quietly. So each step of the
/// stream is awaited inside <see cref="ToolAdmissionAccessor.Begin"/> and released before the update is
/// handed on, which also keeps the participant's pipeline from leaking to the consumer.
/// </para>
/// <para>
/// <strong>Known limit: telemetry attribution on a stream.</strong> Arming publishes the participant's
/// external governance attribution (through <c>IAgentExecutionContext.Initialize</c>, the only thing
/// permitted to publish it) in the stream's first step, and that, like any ambient value, is dropped at the
/// first <c>yield</c>. Spans from later steps of a streamed participant run can therefore carry the
/// supervisor's attribution. That affects how spans are labelled, never what is authorized. It is not
/// fixed here by calling <c>BeginTurn</c> again, because a second, nested publication is exactly what
/// <c>AttributionIsPublishedByTheExecutionContextOnlyTests</c> forbids; a real fix gives the execution
/// context itself a safe way to re-publish for the current step (#803).
/// </para>
/// <para>
/// <strong>The participant's trace is folded back</strong> into the supervisor turn's trace when each run
/// ends — including a run that throws — because the child scope's recorder is gone with the scope and the
/// turn result reports the parent's.
/// </para>
/// <para>
/// <strong>Known property: per-run governance state starts fresh.</strong> Each run is its own scope, so the
/// loop guard's call history and anything else the pipeline accumulates per run resets between runs of the
/// same participant. Before this, the whole Magentic turn shared one pipeline and that state accumulated
/// across rounds (under the wrong agent's identity). Delegation has the same property by the same
/// construction. The fix is one scope per (turn, participant) reused across its runs (#804).
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
        var (scope, pipeline) = Arm(agentId, fallbackScopeId);
        await using (scope.ConfigureAwait(false))
        {
            try
            {
                using (ToolAdmissionAccessor.Begin(pipeline))
                    return await inner.RunAsync(messages, session, options, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _parentTrace.Absorb(pipeline.GetTrace());
            }
        }
    }

    private async IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(
        string agentId, string fallbackScopeId, IEnumerable<ChatMessage> messages, AgentSession? session,
        AgentRunOptions? options, AIAgent inner, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (scope, pipeline) = Arm(agentId, fallbackScopeId);
        await using (scope.ConfigureAwait(false))
        {
            var stream = inner.RunStreamingAsync(messages, session, options, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            try
            {
                while (true)
                {
                    // Published for this step only: set and released within one segment of this iterator,
                    // so it is in force while the inner agent runs (its tool calls happen inside
                    // MoveNextAsync) and gone before the update reaches the consumer.
                    bool hasNext;
                    using (ToolAdmissionAccessor.Begin(pipeline))
                        hasNext = await stream.MoveNextAsync().ConfigureAwait(false);

                    if (!hasNext)
                        yield break;

                    yield return stream.Current;
                }
            }
            finally
            {
                // Nested so neither can strand the other: a throwing dispose must not drop the trace.
                try
                {
                    _parentTrace.Absorb(pipeline.GetTrace());
                }
                finally
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    // Mirrors CapabilityMatchSupervisor.ArmDelegationGovernance: a new scope per run, disposed here if
    // arming throws because nothing else has been handed it yet.
    private (AsyncServiceScope Scope, IToolCallAdmissionPipeline Pipeline) Arm(string agentId, string fallbackScopeId)
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

            return (scope, pipeline);
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }
}
