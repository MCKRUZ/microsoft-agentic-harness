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
/// Returns the ordinary <see cref="AgentTurnResult"/> shape a single-agent turn returns, so the one
/// call site that dispatches to this runner (<see cref="ExecuteAgentTurnCommandHandler"/>) can thread
/// the result through the exact same observability recording, token/cost accounting, and context
/// snapshot code every other turn already gets — a Magentic turn is not a second, parallel pipeline.
/// Lives in <c>Application.Core</c> rather than beside <c>IMagenticOrchestrator</c> in
/// <c>Application.AI.Common</c> because it returns <see cref="AgentTurnResult"/>, which
/// <c>Application.AI.Common</c> cannot reference (Application.Core depends on Application.AI.Common,
/// never the reverse).
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
    /// <param name="cancellationToken">Cancellation token for the whole workflow run.</param>
    /// <returns>
    /// The turn's result in the same shape a single-agent turn returns. On workflow failure,
    /// <see cref="AgentTurnResult.Success"/> is <see langword="false"/> with
    /// <see cref="AgentTurnResult.ErrorKind"/> set to <see cref="AgentTurnErrorKind.Internal"/> and
    /// the workflow's own error message.
    /// </returns>
    Task<AgentTurnResult> RunTurnAsync(
        AgentDefinition supervisor,
        string userMessage,
        IReadOnlyList<ChatMessage> conversationHistory,
        CancellationToken cancellationToken);
}
