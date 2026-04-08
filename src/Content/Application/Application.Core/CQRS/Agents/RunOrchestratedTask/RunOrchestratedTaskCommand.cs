using Application.AI.Common.Interfaces.MediatR;
using MediatR;

namespace Application.Core.CQRS.Agents.RunOrchestratedTask;

/// <summary>
/// Runs an orchestrated task where an orchestrator agent decomposes a complex task,
/// delegates subtasks to specialized sub-agents, and synthesizes results.
/// </summary>
public record RunOrchestratedTaskCommand : IRequest<OrchestratedTaskResult>, IAgentScopedRequest
{
	/// <summary>
	/// The orchestrator agent name/skill ID.
	/// </summary>
	public required string OrchestratorName { get; init; }

	/// <summary>
	/// Description of the task to be orchestrated.
	/// </summary>
	public required string TaskDescription { get; init; }

	/// <summary>
	/// Names of sub-agents available for delegation.
	/// </summary>
	public required IReadOnlyList<string> AvailableAgents { get; init; }

	/// <summary>
	/// Maximum total turns across all agents.
	/// </summary>
	public int MaxTotalTurns { get; init; } = 20;

	/// <summary>
	/// Optional callback for streaming progress.
	/// </summary>
	public Func<OrchestrationProgress, Task>? OnProgress { get; init; }

	// IAgentScopedRequest
	public string AgentId => OrchestratorName;
	public string ConversationId { get; init; } = Guid.NewGuid().ToString();
	public int TurnNumber => 0;
}

/// <summary>
/// Result of an orchestrated multi-agent task.
/// </summary>
public record OrchestratedTaskResult
{
	public required bool Success { get; init; }
	public required string FinalSynthesis { get; init; }
	public required IReadOnlyList<SubAgentResult> SubAgentResults { get; init; }
	public int TotalTurns { get; init; }
	public int TotalToolInvocations { get; init; }
	public string? Error { get; init; }
}

/// <summary>
/// Result from a single sub-agent delegation.
/// </summary>
public record SubAgentResult
{
	public required string AgentName { get; init; }
	public required string Subtask { get; init; }
	public required string Result { get; init; }
	public required bool Success { get; init; }
	public int TurnsUsed { get; init; }
	public IReadOnlyList<string> ToolsInvoked { get; init; } = [];
}

/// <summary>
/// Progress update during orchestration.
/// </summary>
public record OrchestrationProgress
{
	public required string Phase { get; init; }
	public required string AgentName { get; init; }
	public required string Status { get; init; }
	public string? Detail { get; init; }
}
