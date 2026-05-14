using System.Diagnostics;
using System.Text;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Application.Core.Workflows.Orchestration;

/// <summary>
/// Creates MAF workflow executor bindings from <see cref="AgentWorkflowStep"/> definitions.
/// Each binding wraps an <see cref="IChatClient"/> call as a fan-out-compatible executor that
/// accepts <see cref="MultiAgentWorkflowInput"/> and produces <see cref="AgentStepResult"/>.
/// </summary>
/// <remarks>
/// <para>
/// Uses <see cref="ExecutorBindingExtensions.BindAsExecutor{TInput, TOutput}"/> to create
/// function-based executors rather than requiring a class per agent. This keeps the workflow
/// graph construction lightweight: one factory call per agent step.
/// </para>
/// <para>
/// For higher-level concurrent agent workflows that operate on <c>AIAgent</c> instances
/// and <see cref="ChatMessage"/> lists directly, see
/// <see cref="AgentWorkflowBuilder.BuildConcurrent(IEnumerable{Microsoft.Agents.AI.AIAgent}, Func{IList{List{ChatMessage}}, List{ChatMessage}}?)"/>.
/// This factory targets the lower-level <see cref="WorkflowBuilder"/> API for type-safe
/// custom DTOs and structured result aggregation.
/// </para>
/// </remarks>
public static class AgentExecutorFactory
{
    /// <summary>
    /// Creates a workflow executor binding for a single agent step.
    /// The executor sends the task description (with optional context) to the chat client
    /// using the step's system prompt and returns a structured <see cref="AgentStepResult"/>.
    /// </summary>
    /// <param name="step">
    /// The agent definition specifying name, system prompt, and optional model override.
    /// </param>
    /// <param name="chatClient">
    /// The <see cref="IChatClient"/> to invoke. Callers are responsible for constructing
    /// the client with the correct model/deployment (honoring <see cref="AgentWorkflowStep.ModelOverride"/>).
    /// </param>
    /// <returns>
    /// An <see cref="ExecutorBinding"/> that can be added to a <see cref="WorkflowBuilder"/>
    /// fan-out edge.
    /// </returns>
    public static ExecutorBinding Create(AgentWorkflowStep step, IChatClient chatClient)
    {
        return CreateHandler(step, chatClient)
            .BindAsExecutor<MultiAgentWorkflowInput, AgentStepResult>(
                $"agent_{step.AgentName}");
    }

    private static Func<MultiAgentWorkflowInput, IWorkflowContext, CancellationToken, ValueTask<AgentStepResult>>
        CreateHandler(AgentWorkflowStep step, IChatClient chatClient)
    {
        return async (input, _, cancellationToken) =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var messages = BuildMessages(step, input);
                var response = await chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
                sw.Stop();

                return new AgentStepResult(
                    step.AgentName,
                    response.Text ?? string.Empty,
                    sw.Elapsed,
                    Succeeded: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sw.Stop();
                return new AgentStepResult(
                    step.AgentName,
                    $"Agent '{step.AgentName}' failed: {ex.Message}",
                    sw.Elapsed,
                    Succeeded: false);
            }
        };
    }

    private static IList<ChatMessage> BuildMessages(AgentWorkflowStep step, MultiAgentWorkflowInput input)
    {
        var messages = new List<ChatMessage>(2)
        {
            new(ChatRole.System, step.SystemPrompt)
        };

        if (input.AdditionalContext is null or { Count: 0 })
        {
            messages.Add(new ChatMessage(ChatRole.User, input.TaskDescription));
            return messages;
        }

        var userContent = new StringBuilder(input.TaskDescription);
        userContent.AppendLine();
        userContent.AppendLine();
        userContent.AppendLine("--- Additional Context ---");

        foreach (var (key, value) in input.AdditionalContext)
        {
            userContent.AppendLine($"[{key}]: {value}");
        }

        messages.Add(new ChatMessage(ChatRole.User, userContent.ToString()));
        return messages;
    }
}
