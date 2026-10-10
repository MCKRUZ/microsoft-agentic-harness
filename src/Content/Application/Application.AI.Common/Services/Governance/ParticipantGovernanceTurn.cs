using System.Runtime.CompilerServices;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// One supervisor turn's worth of participant governance: wraps each participant so its runs are
/// governed as <em>that agent</em>, and owns the child scopes those runs share until the turn ends.
/// Obtained from <see cref="ParticipantGovernance.ForTurn"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One scope per wrapped participant, for the life of this object.</strong> A participant can run
/// many times in one workflow, and the pipeline's per-run state — the loop guard's call history, the
/// aggregate output budget — must accumulate across those rounds or a participant sent back to repeat the
/// same call never trips them. Each wrapped agent arms its scope on its first run and reuses it.
/// </para>
/// <para>
/// <strong>Ending the turn.</strong> <see cref="DisposeAsync"/> folds each armed participant's trace into
/// the supervisor's once (the trace is cumulative, so folding after every round would double-count) and
/// disposes the scopes. It must run before the turn's trace is read; a participant that tries to run
/// after that is refused with <see cref="ObjectDisposedException"/> rather than arming a scope nobody
/// will release. The lifetime is lexical — the turn runner holds this object — so there is no keyed
/// registry to leak between workflows.
/// </para>
/// <para>
/// <strong>A run still in flight when the turn ends.</strong> An engine that returns on cancellation or
/// timeout without awaiting a participant leaves that run holding a scope this object is about to
/// dispose, and its trace is folded before the run finishes. The run's later tool calls are governed by a
/// released scope (not verified to fail closed) and their decisions miss the turn's trace. Nothing waits
/// for in-flight runs: a participant stuck in a tool call would then hold the turn open past its timeout.
/// </para>
/// <para>
/// <strong>Streaming publishes the pipeline around every step.</strong> Ambient (<c>AsyncLocal</c>)
/// state set inside an async iterator does not survive a <c>yield</c>: the consumer resumes the iterator
/// under its own context. A pipeline published once would govern only the first step, so each step is
/// awaited inside <see cref="ToolAdmissionAccessor.Begin(IToolCallAdmissionPipeline, string)"/> and released before the update is handed on.
/// </para>
/// <para>
/// <strong>Attribution is re-asserted the same way.</strong> The Agent 365 attribution the participant's
/// context publishes while arming lives only in the first step, so each step also calls
/// <see cref="IAgentExecutionContext.ReassertAttribution"/> and releases it with the pipeline (#803).
/// </para>
/// </remarks>
public sealed class ParticipantGovernanceTurn : IAsyncDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAgentExecutionContext _parentContext;
    private readonly IGovernanceTraceRecorder _parentTrace;
    private readonly string _conversationId;
    private readonly object _gate = new();
    private readonly List<ParticipantScope> _participants = [];
    private bool _ended;

    internal ParticipantGovernanceTurn(
        IServiceScopeFactory scopeFactory,
        IAgentExecutionContext parentContext,
        IGovernanceTraceRecorder parentTrace,
        string conversationId)
    {
        _scopeFactory = scopeFactory;
        _parentContext = parentContext;
        _parentTrace = parentTrace;
        _conversationId = conversationId;
    }

    /// <summary>Returns <paramref name="agent"/> wrapped so every run is governed as <paramref name="agentId"/>.</summary>
    /// <param name="agent">The participant.</param>
    /// <param name="agentId">The participant's own agent id: the identity its tool calls are authorized as.</param>
    /// <exception cref="ObjectDisposedException">The turn has ended.</exception>
    public AIAgent Wrap(AIAgent agent, string agentId)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var participant = new ParticipantScope(this, agentId);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_ended, this);
            _participants.Add(participant);
        }

        return agent.AsBuilder()
            .Use(
                runFunc: (messages, session, options, inner, ct) =>
                    RunAsync(participant, messages, session, options, inner, ct),
                runStreamingFunc: (messages, session, options, inner, ct) =>
                    RunStreamingAsync(participant, messages, session, options, inner, ct))
            .Build();
    }

    /// <summary>
    /// Ends the turn: folds each armed participant's trace into the supervisor's (once) and disposes the
    /// scopes. Idempotent. A participant whose fold fails still has its scope released, and the others are
    /// still released; the failures are rethrown together.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        ParticipantScope[] ending;
        lock (_gate)
        {
            _ended = true;
            ending = [.. _participants];
            _participants.Clear();
        }

        List<Exception>? failures = null;
        foreach (var participant in ending)
        {
            try
            {
                await participant.EndAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }
        }

        if (failures is not null)
            throw new AggregateException(failures);
    }

    private static async Task<AgentResponse> RunAsync(
        ParticipantScope participant, IEnumerable<ChatMessage> messages, AgentSession? session,
        AgentRunOptions? options, AIAgent inner, CancellationToken cancellationToken)
    {
        using (participant.BeginStep())
            return await inner.RunAsync(messages, session, options, cancellationToken).ConfigureAwait(false);
    }

    private static async IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(
        ParticipantScope participant, IEnumerable<ChatMessage> messages, AgentSession? session,
        AgentRunOptions? options, AIAgent inner, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = inner.RunStreamingAsync(messages, session, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            // Set and released within one segment of this iterator: in force while the inner agent runs
            // (its tool calls happen inside MoveNextAsync), gone before the update reaches the consumer.
            bool hasNext;
            using (participant.BeginStep())
                hasNext = await stream.MoveNextAsync().ConfigureAwait(false);

            if (!hasNext)
                yield break;

            yield return stream.Current;
        }
    }

    /// <summary>Releases a step's admission pipeline, then its attribution — the reverse of how they were taken.</summary>
    private sealed class StepScope(IDisposable admission, IDisposable attribution) : IDisposable
    {
        public void Dispose()
        {
            admission.Dispose();
            attribution.Dispose();
        }
    }

    /// <summary>
    /// One wrapped participant's child scope and pipeline: armed lazily on its first run, shared by the
    /// rest, folded and disposed when the turn ends.
    /// </summary>
    private sealed class ParticipantScope(ParticipantGovernanceTurn turn, string agentId)
    {
        private readonly object _lock = new();
        private AsyncServiceScope _scope;
        private IToolCallAdmissionPipeline? _pipeline;
        private IAgentExecutionContext? _context;
        private bool _ended;

        public string AgentId => agentId;

        /// <summary>
        /// Puts this participant's governance in force for the code about to run — its admission pipeline
        /// (with its agent id) and its Agent 365 attribution — arming the scope on first use. Dispose the
        /// result when that code ends; both are released in reverse order. Called around every step of a
        /// stream: ambient state set inside an async iterator does not survive a <c>yield</c>, and the
        /// attribution <c>Initialize</c> published while arming lives only in the first step (#803).
        /// </summary>
        public IDisposable BeginStep()
        {
            IToolCallAdmissionPipeline pipeline;
            IAgentExecutionContext context;
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_ended, turn);
                _pipeline ??= Arm();
                pipeline = _pipeline;
                context = _context!;
            }

            var attribution = context.ReassertAttribution();
            return new StepScope(ToolAdmissionAccessor.Begin(pipeline, agentId), attribution);
        }

        public async ValueTask EndAsync()
        {
            IToolCallAdmissionPipeline? pipeline;
            lock (_lock)
            {
                _ended = true;
                pipeline = _pipeline;
            }

            if (pipeline is null)
                return;

            try
            {
                turn._parentTrace.Absorb(pipeline.GetTrace());
            }
            finally
            {
                await _scope.DisposeAsync().ConfigureAwait(false);
            }
        }

        // The same open/arm/dispose-on-throw sequence as CapabilityMatchSupervisor.ArmDelegationGovernance,
        // but held for the turn rather than one run — keep the arming itself in step until a shared
        // governed-child-scope handle replaces both.
        private IToolCallAdmissionPipeline Arm()
        {
            var scope = turn._scopeFactory.CreateAsyncScope();
            try
            {
                var pipeline = GovernanceArmer.ArmWithAdmission(
                    scope.ServiceProvider,
                    agentId,
                    GovernanceArmingPolicy.Delegation,
                    turn._parentContext,
                    turn._conversationId);

                _scope = scope;
                _context = scope.ServiceProvider.GetRequiredService<IAgentExecutionContext>();

                // Arming published the participant's attribution and the context holds it for the turn. This
                // runs inside an iterator step, so that scope cannot be held across the yield: in a shared
                // baggage holder it would leave the participant's identity on the supervisor's spans until
                // the turn ended. Release it here, in the flow that took it; every step re-asserts instead.
                _context.ReleaseTurnAttribution();
                return pipeline;
            }
            catch
            {
                scope.Dispose();
                throw;
            }
        }
    }
}
