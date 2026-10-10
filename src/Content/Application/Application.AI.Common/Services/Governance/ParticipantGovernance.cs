using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// Governs an agent that an orchestration engine runs on its own schedule — a Magentic participant — as
/// <em>that agent</em>: each participant's runs share a child scope, armed with the agent's own id, whose
/// admission pipeline is the one ambient for every tool call those runs make.
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
/// call-once tool the supervisor claimed stays claimed.
/// </para>
/// <para>
/// <strong>The participant's trace is folded</strong> into the supervisor turn's trace when the turn ends,
/// including for a run that threw: the child scope's recorder is gone with the scope, and the turn result
/// reports the parent's. See <see cref="ParticipantGovernanceTurn"/> for the lifetime.
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
    /// <param name="scopeFactory">Opens the child scope each participant is armed in.</param>
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

    /// <summary>
    /// Starts governance for one supervisor turn. The caller owns the result and must dispose it, before
    /// the turn's trace is read, to fold the participants' traces in and release their scopes.
    /// </summary>
    /// <param name="conversationId">
    /// Conversation id and call-once scope to use where the parent supplies none; the supervisor's
    /// conversation id.
    /// </param>
    public ParticipantGovernanceTurn ForTurn(string conversationId)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        return new ParticipantGovernanceTurn(_scopeFactory, _parentContext, _parentTrace, conversationId);
    }
}
