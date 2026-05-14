using Application.AI.Common.Interfaces;
using Domain.Common.Config.AI;
using Microsoft.Agents.AI.Workflows;

namespace Application.Core.Workflows.Orchestration;

/// <summary>
/// Factory for building a multi-agent fan-out/fan-in workflow from a list of
/// <see cref="AgentWorkflowStep"/> definitions. The resulting <see cref="Workflow"/>
/// runs all agents in parallel on the same <see cref="MultiAgentWorkflowInput"/>,
/// waits for all to complete, then aggregates results into
/// <see cref="AggregatedAgentResults"/>.
/// </summary>
/// <remarks>
/// <para>
/// This creates a predefined, known-shape pipeline where the agents and their
/// arrangement are determined at build time. For dynamic orchestration where an LLM
/// decides at runtime which agents to invoke, see
/// <c>RunOrchestratedTaskCommandHandler</c> in the CQRS layer.
/// </para>
/// <para>
/// Workflow graph structure:
/// <code>
///     [start] ──fan-out──> [agent_A]
///                       ──> [agent_B]    ──fan-in barrier──> [aggregate_results]
///                       ──> [agent_C]
/// </code>
/// </para>
/// <para>
/// For concurrent agent workflows using the higher-level <see cref="AgentWorkflowBuilder"/>
/// API with <c>AIAgent</c> instances and <see cref="Microsoft.Extensions.AI.ChatMessage"/>
/// lists, see <see cref="AgentWorkflowBuilder.BuildConcurrent"/>.
/// </para>
/// </remarks>
public static class MultiAgentWorkflow
{
    /// <summary>
    /// Creates a workflow that runs the specified agents in parallel on the same input,
    /// then aggregates their results through a fan-in barrier.
    /// </summary>
    /// <param name="agents">
    /// The agent definitions to include in the fan-out. Must contain at least one agent.
    /// Each agent gets its own <see cref="Microsoft.Extensions.AI.IChatClient"/> from the factory.
    /// </param>
    /// <param name="chatClientFactory">
    /// Factory for creating chat clients. Each agent's client is created using
    /// <see cref="AIAgentFrameworkClientType.AzureOpenAI"/> with the step's
    /// <see cref="AgentWorkflowStep.ModelOverride"/> (or <c>"gpt-4o"</c> as default deployment).
    /// </param>
    /// <param name="clientType">
    /// The AI provider to use for all agents. Defaults to <see cref="AIAgentFrameworkClientType.AzureOpenAI"/>.
    /// </param>
    /// <param name="defaultDeployment">
    /// Default model deployment name used when <see cref="AgentWorkflowStep.ModelOverride"/>
    /// is <see langword="null"/>. Defaults to <c>"gpt-4o"</c>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for async chat client creation.</param>
    /// <returns>A configured <see cref="Workflow"/> ready for execution.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="agents"/> is empty.</exception>
    public static async Task<Workflow> BuildAsync(
        IReadOnlyList<AgentWorkflowStep> agents,
        IChatClientFactory chatClientFactory,
        AIAgentFrameworkClientType clientType = AIAgentFrameworkClientType.AzureOpenAI,
        string defaultDeployment = "gpt-4o",
        CancellationToken cancellationToken = default)
    {
        if (agents.Count == 0)
            throw new ArgumentException("At least one agent step is required.", nameof(agents));

        var start = ((Func<MultiAgentWorkflowInput, MultiAgentWorkflowInput>)(input => input))
            .BindAsExecutor<MultiAgentWorkflowInput, MultiAgentWorkflowInput>("workflow_start");

        var agentBindings = await CreateAgentBindingsAsync(
            agents, chatClientFactory, clientType, defaultDeployment, cancellationToken);

        var aggregator = new AggregateResultsExecutor();

        var builder = new WorkflowBuilder(start)
            .AddFanOutEdge(start, agentBindings)
            .AddFanInBarrierEdge(agentBindings, aggregator)
            .WithOutputFrom(aggregator)
            .WithName("multi_agent_fan_out_fan_in")
            .WithDescription(
                $"Parallel execution of {agents.Count} agents with result aggregation");

        return builder.Build();
    }

    private static async Task<ExecutorBinding[]> CreateAgentBindingsAsync(
        IReadOnlyList<AgentWorkflowStep> agents,
        IChatClientFactory chatClientFactory,
        AIAgentFrameworkClientType clientType,
        string defaultDeployment,
        CancellationToken cancellationToken)
    {
        var tasks = agents.Select(async step =>
        {
            var deployment = step.ModelOverride ?? defaultDeployment;
            var chatClient = await chatClientFactory.GetChatClientAsync(
                clientType, deployment, cancellationToken);
            return AgentExecutorFactory.Create(step, chatClient);
        });

        return await Task.WhenAll(tasks);
    }
}
