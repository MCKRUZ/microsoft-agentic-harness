using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Domain.AI.Agents;
using Microsoft.Extensions.AI;

namespace Application.Core.Orchestration.Magentic;

/// <summary>
/// Runs one live conversation turn for an <see cref="AgentOrchestrationMode.Magentic"/> supervisor
/// agent, via <c>IMagenticOrchestrator</c> — the counterpart to a normal single-agent turn's direct
/// <c>AIAgent.RunAsync</c> call. Resolves the manager and every participant named in
/// <see cref="AgentDefinition.Participants"/> the same way any agent is resolved (through the
/// registry, then built from its own skills), so a supervisor and its specialists are ordinary
/// agents — nothing about how they're defined changes because they happen to run inside a Magentic
/// workflow instead of alone.
/// </summary>
/// <remarks>
/// <para>
/// Returns the ordinary <see cref="AgentTurnResult"/> shape a single-agent turn returns, so the one
/// call site that dispatches to this runner (<see cref="ExecuteAgentTurnCommandHandler"/>) can thread
/// the result through the same message recording, tool-execution metrics, and token/cost accounting
/// every other turn already gets — a Magentic turn is not a second, parallel pipeline for those.
/// Lives in <c>Application.Core</c> rather than beside <c>IMagenticOrchestrator</c> in
/// <c>Application.AI.Common</c> because it returns <see cref="AgentTurnResult"/>, which
/// <c>Application.AI.Common</c> cannot reference (Application.Core depends on Application.AI.Common,
/// never the reverse).
/// </para>
/// <para>
/// <strong>Known v1 limitations, named rather than silently accepted:</strong>
/// </para>
/// <list type="bullet">
/// <item><description>
/// <strong>No per-tool-call replay records.</strong> See <c>MagenticAgentTurnRunner</c>'s own remarks —
/// this runner always returns an empty <see cref="AgentTurnResult.ToolCalls"/>.
/// </description></item>
/// <item><description>
/// <strong>The context-snapshot registration breakdown (system prompt / skills / tools / MCP /
/// sub-agents) is always empty for a Magentic turn**, not just incomplete.</strong> A single-agent turn
/// builds its agent through <c>IAgentConversationCache.GetOrCreateAsync</c>, which is what populates the
/// per-conversation context the snapshot's registration breakdown reads from
/// (<c>ExecuteAgentTurnCommandHandler.BuildRegistrationSnapshot</c> via <c>_agentCache.TryGetContext</c>).
/// This runner builds the manager and participants directly through <c>IAgentFactory</c> instead (see
/// <c>MagenticAgentTurnRunner.BuildAgentAsync</c>), so that cache is never populated for a Magentic
/// conversation — the snapshot's message/tool-name recording and token/cost accounting are genuinely
/// shared with the single-agent path; the registration breakdown is not.
/// </description></item>
/// <item><description>
/// <strong>No incremental streaming.</strong> A Magentic turn blocks until the whole workflow (including
/// any HITL plan-review pause) finishes before returning any text — unlike a single-agent turn, which
/// streams deltas via <c>AgentTurnStreamSink</c> when a transport has attached one.
/// </description></item>
/// <item><description>
/// <strong>Conversation history is re-flattened into a fresh string every turn</strong> (see
/// <c>MagenticAgentTurnRunner.BuildTask</c>) because <c>MagenticWorkflowRequest.Task</c> takes a single
/// string, not a message list — unlike the single-agent path's stable <c>ChatMessage</c> list, this
/// defeats provider-side prompt caching on the (growing, unbounded) history for every Magentic turn.
/// Inherent to the wrapped framework's task-ledger model, not a bug in this runner.
/// </description></item>
/// </list>
/// </remarks>
public interface IMagenticAgentTurnRunner
{
    /// <summary>
    /// Runs the supervisor's Magentic workflow for one turn.
    /// </summary>
    /// <param name="supervisor">
    /// The supervisor's own <see cref="AgentDefinition"/> — must have
    /// <see cref="AgentDefinition.OrchestrationMode"/> equal to
    /// <see cref="AgentOrchestrationMode.Magentic"/> and a non-empty
    /// <see cref="AgentDefinition.Participants"/> list.
    /// </param>
    /// <param name="userMessage">The user's message for this turn.</param>
    /// <param name="conversationHistory">
    /// Prior turns, folded into the workflow's task description as context — a Magentic workflow
    /// takes a single task string, not a message list, so each live turn starts a fresh workflow run
    /// rather than resuming an in-progress one across turns.
    /// </param>
    /// <param name="overrides">
    /// Per-turn overrides carried on the request itself (as opposed to <see cref="AgentDefinition"/>,
    /// which is per-agent and shared across every turn) — applied to the manager only, the same way a
    /// single-agent turn applies them only to the one agent the caller addressed.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for the whole workflow run.</param>
    /// <returns>
    /// The turn's result in the same shape a single-agent turn returns. On workflow failure,
    /// <see cref="AgentTurnResult.Success"/> is <see langword="false"/> with
    /// <see cref="AgentTurnResult.ErrorKind"/> set to <see cref="AgentTurnErrorKind.Internal"/> and a
    /// generic error message — the workflow's own error text is logged, never returned to the caller
    /// (it can carry a raw exception message; see <c>MagenticAgentTurnRunner.RunTurnAsync</c>).
    /// </returns>
    Task<AgentTurnResult> RunTurnAsync(
        AgentDefinition supervisor,
        string userMessage,
        IReadOnlyList<ChatMessage> conversationHistory,
        MagenticTurnOverrides overrides,
        CancellationToken cancellationToken);
}

/// <summary>
/// Per-turn overrides a caller can set on an individual request, distinct from the supervisor's own
/// <see cref="AgentDefinition"/> — mirrors the subset of <c>ExecuteAgentTurnCommand</c>'s own override
/// fields that apply to a single addressed agent. All optional; a default-constructed instance applies
/// no overrides.
/// </summary>
public sealed record MagenticTurnOverrides
{
    /// <summary>No overrides — every field left at its manifest/provider default.</summary>
    public static readonly MagenticTurnOverrides None = new();

    /// <summary>Additional system-prompt context appended to the manager's base instructions.</summary>
    public string? SystemPromptOverride { get; init; }

    /// <summary>Deployment/model override for the manager, taking precedence over its declared default.</summary>
    public string? DeploymentOverride { get; init; }

    /// <summary>Sampling temperature override for the manager. Null preserves the provider default.</summary>
    public float? Temperature { get; init; }

    /// <summary>
    /// Caller-supplied per-turn context, carried via the same ambient <c>CallerTurnContextScope</c>
    /// rail a single-agent turn uses — applies to every model call the Magentic workflow makes
    /// (manager and participants alike), not just the manager, since it travels ambiently rather than
    /// through the manager's own construction.
    /// </summary>
    public string? TurnContext { get; init; }
}
