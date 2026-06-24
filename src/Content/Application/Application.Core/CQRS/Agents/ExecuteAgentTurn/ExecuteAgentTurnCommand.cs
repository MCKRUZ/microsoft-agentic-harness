using Application.AI.Common.Interfaces.MediatR;
using Application.Common.Interfaces.MediatR;
using Domain.AI.Models;
using MediatR;
using Microsoft.Extensions.AI;

namespace Application.Core.CQRS.Agents.ExecuteAgentTurn;

/// <summary>
/// Executes a single turn of an agent: sends a user message, the agent responds
/// (potentially invoking tools), and returns the response.
/// </summary>
/// <remarks>
/// Uses a 5-minute timeout to accommodate multi-step tool call chains.
/// The default 30s MediatR timeout is too short for agentic workloads.
/// </remarks>
public record ExecuteAgentTurnCommand : IRequest<AgentTurnResult>, IAgentTurnRequest, IHasTimeout, IContentScreenable, IHasObservabilitySession, IConsumesTokens
{
	/// <summary>
	/// Pre-flight token estimate: the user message at roughly 4 characters per token plus a
	/// base allowance covering the system prompt, conversation history, and a single completion.
	/// Deliberately conservative — actual usage is reconciled after the turn via the tracker.
	/// </summary>
	public int EstimatedTokenCost => (UserMessage.Length / 4) + 1_000;

	/// <inheritdoc />
	public string ContentToScreen => UserMessage;

	/// <inheritdoc />
	public ContentScreeningTarget ScreeningTarget => ContentScreeningTarget.Input;
	/// <inheritdoc/>
	public TimeSpan? Timeout => TimeSpan.FromMinutes(5);

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
	/// Optional system prompt override appended to the skill's base instructions.
	/// </summary>
	public string? SystemPromptOverride { get; init; }

	/// <summary>
	/// Optional deployment/model override — takes precedence over the skill's
	/// declared deployment and <c>AppConfig.AI.AgentFramework.DefaultDeployment</c>.
	/// </summary>
	public string? DeploymentOverride { get; init; }

	/// <summary>
	/// Optional sampling temperature. Null preserves provider defaults.
	/// </summary>
	public float? Temperature { get; init; }

	// IAgentScopedRequest / IAgentTurnRequest
	public string AgentId => AgentName;
	public string ConversationId { get; init; } = Guid.NewGuid().ToString();
	public int TurnNumber { get; init; }

	/// <summary>
	/// Database session ID for correlating observability records.
	/// Set by <see cref="RunConversation.RunConversationCommandHandler"/>
	/// when persistence is enabled.
	/// </summary>
	public Guid ObservabilitySessionId { get; init; }
}

/// <summary>
/// Result of a single agent turn execution.
/// </summary>
public record AgentTurnResult : IAgentTurnResult
{
	public required bool Success { get; init; }
	public required string Response { get; init; }
	public required IReadOnlyList<ChatMessage> UpdatedHistory { get; init; }
	public IReadOnlyList<string> ToolsInvoked { get; init; } = [];
	public string? Error { get; init; }

	/// <summary>
	/// Classifies a failed turn so transports can decide how to surface <see cref="Error"/>.
	/// <see cref="AgentTurnErrorKind.Configuration"/> errors carry an actionable, secret-free
	/// message that may be shown to developers; other kinds stay generic in production.
	/// </summary>
	public AgentTurnErrorKind ErrorKind { get; init; } = AgentTurnErrorKind.None;

	// Token usage captured from the LLM calls during this turn
	public int InputTokens { get; init; }
	public int OutputTokens { get; init; }
	public int CacheRead { get; init; }
	public int CacheWrite { get; init; }
	public decimal CostUsd { get; init; }
	public string? Model { get; init; }

	/// <summary>
	/// Snapshot of the per-invocation governance decisions the agent's tool calls passed through
	/// this turn. Null when tool-invocation governance was not engaged. Lets evaluation grade the
	/// agent's governance behaviour independently of task outcome.
	/// </summary>
	public Domain.AI.Governance.GovernanceTrace? Governance { get; init; }
}

/// <summary>
/// Classifies why an agent turn failed, so transports can choose how much detail to surface.
/// </summary>
public enum AgentTurnErrorKind
{
	/// <summary>The turn succeeded, or no classification applies.</summary>
	None,

	/// <summary>
	/// The AI provider is not configured (missing endpoint, API key, or registered client).
	/// The accompanying message is actionable and secret-free.
	/// </summary>
	Configuration,

	/// <summary>An unexpected internal error occurred during the turn.</summary>
	Internal,

	/// <summary>
	/// The turn was cancelled via the caller's token (e.g. the client disconnected).
	/// Routine control flow, not an agent failure — transports should abort quietly
	/// rather than recording a health error or surfacing a failure to the user. A
	/// per-request timeout is distinct: it cancels a linked token, surfaces as a
	/// <see cref="System.TimeoutException"/>, and remains an <see cref="Internal"/> error.
	/// </summary>
	Cancelled
}
