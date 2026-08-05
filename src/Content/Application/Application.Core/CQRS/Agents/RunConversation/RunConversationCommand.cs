using Application.Common.Interfaces.MediatR;
using MediatR;

namespace Application.Core.CQRS.Agents.RunConversation;

/// <summary>
/// Runs a full multi-turn conversation with a standalone agent.
/// The agent processes each message, potentially using tools, and continues
/// until the conversation is complete or max turns is reached.
/// </summary>
/// <remarks>
/// Does NOT implement <c>IAgentScopedRequest</c>. Agent context is set per-turn by each
/// <see cref="ExecuteAgentTurn.ExecuteAgentTurnCommand"/> dispatch, preventing double-initialization
/// of the scoped <c>AgentExecutionContext</c>.
/// </remarks>
public record RunConversationCommand : IRequest<ConversationResult>, IHasTimeout
{
	/// <inheritdoc/>
	/// <remarks>10 minutes: up to <see cref="MaxTurns"/> agent turns, each potentially using tools.</remarks>
	public TimeSpan? Timeout => TimeSpan.FromMinutes(10);

	/// <summary>
	/// The agent to run the conversation with.
	/// </summary>
	public required string AgentName { get; init; }

	/// <summary>
	/// Optional system prompt override for this conversation.
	/// When set, takes precedence over the agent's default system prompt.
	/// </summary>
	public string? SystemPrompt { get; init; }

	/// <summary>
	/// Initial user messages to seed the conversation.
	/// </summary>
	public required IReadOnlyList<string> UserMessages { get; init; }

	/// <summary>
	/// Maximum number of turns before stopping.
	/// </summary>
	public int MaxTurns { get; init; } = 10;

	/// <summary>
	/// Optional callback for streaming turn-by-turn progress.
	/// </summary>
	public Func<TurnProgress, Task>? OnProgress { get; init; }

	/// <summary>
	/// Conversation identifier shared across all turns.
	/// </summary>
	public string ConversationId { get; init; } = Guid.NewGuid().ToString();

	/// <summary>
	/// The owner of the durable transcript this conversation belongs to. Setting it makes the run
	/// <em>continue</em> a conversation rather than start a throwaway one: prior turns are loaded and
	/// replayed to the model, each turn is persisted as it completes, and the whole run holds the
	/// conversation's turn lease. Leaving it null keeps the original behaviour — nothing is read and
	/// nothing is written.
	/// </summary>
	/// <remarks>
	/// <para>
	/// This is the caller's authenticated identity, not a value the caller chooses. It is passed
	/// straight to <c>IConversationStore</c>, which refuses a conversation owned by anyone else; the
	/// handler never compares owners itself. A blank string is rejected rather than treated as "no
	/// owner", because an absent identity has repeatedly been read as global access in this codebase —
	/// omit the property to opt out, do not blank it.
	/// </para>
	/// <para>
	/// <strong><see cref="MaxTurns"/> and the seed-message cap bound this run, not the conversation.</strong>
	/// A durable conversation outlives any one run, so a per-run ceiling cannot also be its lifetime
	/// ceiling — what bounds total length is the conversation-lifetime token budget, which is durable and
	/// spans every run. Reading <see cref="MaxTurns"/> as a conversation limit would cap a long-lived
	/// session at a number chosen for a single request.
	/// </para>
	/// </remarks>
	public string? ConversationOwnerId { get; init; }
}

/// <summary>
/// Result of a complete conversation.
/// </summary>
public record ConversationResult
{
	public required bool Success { get; init; }
	public required IReadOnlyList<TurnSummary> Turns { get; init; }
	public required string FinalResponse { get; init; }
	public int TotalToolInvocations { get; init; }
	public string? Error { get; init; }

	/// <summary>
	/// Input+output tokens consumed across every turn of this conversation.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The handler folds the same figure into the conversation-lifetime budget under this
	/// conversation's own id, and — since issue #245 — deliberately does <em>not</em> release that
	/// entry when it returns, because a conversation now outlives the run that happened to carry it.
	/// Surfacing the total lets a caller that owns a <em>larger</em> unit of work than one conversation
	/// — a plan run spanning many single-conversation steps — accumulate spend against its own budget
	/// key without reading the entry this handler writes.
	/// </para>
	/// <para>
	/// <strong>Not an exact meter on the failure path.</strong> A turn that fails returns before its
	/// usage is folded into the running totals, so a conversation that burned tokens and then errored
	/// reports a <em>partial</em> total: every turn that already succeeded is counted, only the
	/// failing turn's usage is missing. The figure is zero only in the special case where the first
	/// turn is the one that fails. A caller metering spend across many conversations therefore
	/// under-counts each failed one by its final turn. That is a deliberate consequence of the
	/// existing early-return, not a guarantee: treat this as the accounted total, not the billed
	/// total.
	/// </para>
	/// </remarks>
	public int TotalTokens { get; init; }

	/// <summary>
	/// True when the conversation stopped early because it exhausted its lifetime token budget
	/// (<c>AppConfig.AI.AgentFramework.ConversationTokenBudget</c>). This is a graceful stop, not a
	/// failure: <see cref="Success"/> stays true and <see cref="Turns"/> holds the turns that ran.
	/// </summary>
	public bool BudgetExhausted { get; init; }

	/// <summary>
	/// Aggregated snapshot of the per-invocation governance decisions across all turns of the
	/// conversation. Null when tool-invocation governance was not engaged.
	/// </summary>
	public Domain.AI.Governance.GovernanceTrace? Governance { get; init; }
}

/// <summary>
/// Summary of a single turn within a conversation.
/// </summary>
public record TurnSummary
{
	public required int TurnNumber { get; init; }
	public required string UserMessage { get; init; }
	public required string AgentResponse { get; init; }
	public IReadOnlyList<string> ToolsInvoked { get; init; } = [];
}

/// <summary>
/// Progress update during conversation execution.
/// </summary>
public record TurnProgress
{
	public required int TurnNumber { get; init; }
	public required string AgentName { get; init; }
	public required string Status { get; init; }
	public string? Response { get; init; }
}
