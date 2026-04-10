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
