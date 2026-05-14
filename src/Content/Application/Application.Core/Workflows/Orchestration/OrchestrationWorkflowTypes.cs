namespace Application.Core.Workflows.Orchestration;

/// <summary>
/// Defines an agent to include in a multi-agent fan-out/fan-in workflow.
/// Each step maps to a single executor that sends the task to a chat client
/// with the specified system prompt.
/// </summary>
/// <param name="AgentName">
/// Unique name identifying this agent within the workflow. Used as the executor ID suffix
/// and included in <see cref="AgentStepResult.AgentName"/> for traceability.
/// </param>
/// <param name="SystemPrompt">
/// System-level instructions sent to the chat client before the task description.
/// Should define the agent's role, expertise, and output format expectations.
/// </param>
/// <param name="ModelOverride">
/// Optional model deployment name that overrides the default. When <see langword="null"/>,
/// the chat client uses whatever model the <see cref="Microsoft.Extensions.AI.IChatClient"/>
/// was constructed with.
/// </param>
public sealed record AgentWorkflowStep(
    string AgentName,
    string SystemPrompt,
    string? ModelOverride = null);

/// <summary>
/// Input to the multi-agent fan-out/fan-in workflow. The <see cref="TaskDescription"/>
/// is sent to every parallel agent, optionally enriched with <see cref="AdditionalContext"/>.
/// </summary>
/// <param name="TaskDescription">
/// The task or question that all parallel agents will process independently.
/// </param>
/// <param name="AdditionalContext">
/// Optional key-value pairs appended to the user message as structured context.
/// Useful for passing domain-specific data (document excerpts, metadata) without
/// modifying the task description itself.
/// </param>
public sealed record MultiAgentWorkflowInput(
    string TaskDescription,
    IReadOnlyDictionary<string, string>? AdditionalContext = null);

/// <summary>
/// Output from a single agent executor in the fan-out phase. One instance is produced
/// per <see cref="AgentWorkflowStep"/> and streamed to the fan-in aggregator.
/// </summary>
/// <param name="AgentName">The name of the agent that produced this result.</param>
/// <param name="Output">
/// The text response from the agent. Empty string on failure (never <see langword="null"/>).
/// </param>
/// <param name="Duration">Wall-clock time the agent took to produce its response.</param>
/// <param name="Succeeded">
/// <see langword="true"/> when the chat client returned a response without throwing;
/// <see langword="false"/> when the agent call failed (see <see cref="Output"/> for the error message).
/// </param>
public sealed record AgentStepResult(
    string AgentName,
    string Output,
    TimeSpan Duration,
    bool Succeeded);

/// <summary>
/// Aggregated output from all parallel agents after the fan-in barrier completes.
/// Produced by the <see cref="AggregateResultsExecutor"/> by incrementally collecting
/// individual <see cref="AgentStepResult"/> messages.
/// </summary>
/// <param name="Results">All individual agent results in the order they were received.</param>
/// <param name="CombinedOutput">
/// Concatenation of all successful agent outputs, separated by double newlines.
/// Each section is prefixed with the agent name for identification.
/// </param>
/// <param name="SucceededCount">Number of agents that completed successfully.</param>
/// <param name="FailedCount">Number of agents that failed.</param>
public sealed record AggregatedAgentResults(
    IReadOnlyList<AgentStepResult> Results,
    string CombinedOutput,
    int SucceededCount,
    int FailedCount);
