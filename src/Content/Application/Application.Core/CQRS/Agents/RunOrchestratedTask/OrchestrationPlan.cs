using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Application.Core.CQRS.Agents.RunOrchestratedTask;

/// <summary>
/// The orchestrator's decomposition of a task: which agent handles which piece, in order. The shape the
/// planning call asks the model for (via <c>IStructuredOutputInvoker</c>) and the handler reads.
/// </summary>
/// <remarks>
/// Replaces a free-text <c>SUBTASK: agent - description</c> convention that was scraped line by line.
/// That parser silently dropped lines it could not read and, when it found none, handed the whole task to
/// the first available agent, so an unusable plan looked like a confident one. A typed plan either parses
/// (after the invoker's one repair attempt) or is rejected, and the handler validates the agent names
/// against the ones it was actually given.
/// </remarks>
public sealed record OrchestrationPlan
{
    /// <summary>The subtasks, in the order they should run.</summary>
    [JsonPropertyName("subtasks")]
    [Description("The subtasks that together accomplish the task, in the order they should run.")]
    public required IReadOnlyList<PlannedSubtask> Subtasks { get; init; }
}

/// <summary>One piece of an <see cref="OrchestrationPlan"/>: an agent and what it is asked to do.</summary>
public sealed record PlannedSubtask
{
    /// <summary>The agent that handles the subtask; must be one of the available agents.</summary>
    [JsonPropertyName("agent")]
    [Description("The name of the agent that handles this subtask, exactly as listed under Available Agents.")]
    public required string Agent { get; init; }

    /// <summary>What the agent is asked to do.</summary>
    [JsonPropertyName("description")]
    [Description("A complete instruction the agent can act on without further context.")]
    public required string Description { get; init; }
}
