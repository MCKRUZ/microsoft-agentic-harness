using Application.AI.Common.Interfaces.MediatR;
using MediatR;
using Microsoft.Extensions.AI;

namespace Application.Core.CQRS.Agents.ExecuteAgentTurn;

/// <summary>
/// Executes a single turn of an agent: sends a user message, the agent responds
/// (potentially invoking tools), and returns the response.
/// </summary>
public record ExecuteAgentTurnCommand : IRequest<AgentTurnResult>, IAgentScopedRequest
{
	/// <summary>
	/// The agent to execute the turn with. Must match a skill ID
	/// that can be resolved by the agent factory.
	/// </summary>
	public required string AgentName { get; init; }

	/// <summary>
	/// The user's message for this turn.
	/// </summary>
	public required string UserMessage { get; init; }

	/// <summary>
	/// Conversation history from previous turns. Empty for the first turn.
	/// </summary>
	public IReadOnlyList<ChatMessage> ConversationHistory { get; init; } = [];

	/// <summary>
	/// Optional system prompt override.
	/// </summary>
	public string? SystemPromptOverride { get; init; }

	// IAgentScopedRequest
	public string AgentId => AgentName;
	public string ConversationId { get; init; } = Guid.NewGuid().ToString();
	public int TurnNumber { get; init; }
}

/// <summary>
/// Result of a single agent turn execution.
/// </summary>
public record AgentTurnResult
{
	public required bool Success { get; init; }
	public required string Response { get; init; }
	public required IReadOnlyList<ChatMessage> UpdatedHistory { get; init; }
	public IReadOnlyList<string> ToolsInvoked { get; init; } = [];
	public string? Error { get; init; }
}
