using System.Diagnostics;
using Application.AI.Common.Interfaces.Planner;
using Application.Core.CQRS.Agents.RunConversation;
using Domain.AI.Planner;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Planner.StepExecutors;

/// <summary>
/// Executes LLM inference steps by delegating to <see cref="RunConversationCommand"/>.
/// </summary>
public sealed class LlmCallStepExecutor : IPlanStepExecutor
{
    private readonly ISender _sender;
    private readonly IPlanProgressNotifier _notifier;
    private readonly PlanExecutionContext _executionContext;
    private readonly ILogger<LlmCallStepExecutor> _logger;

    public LlmCallStepExecutor(
        ISender sender,
        IPlanProgressNotifier notifier,
        PlanExecutionContext executionContext,
        ILogger<LlmCallStepExecutor> logger)
    {
        _sender = sender;
        _notifier = notifier;
        _executionContext = executionContext;
        _logger = logger;
    }

    public async Task<StepExecutionResult> ExecuteAsync(
        PlanStep step,
        IReadOnlyDictionary<PlanStepId, string> upstreamOutputs,
        CancellationToken ct)
    {
        if (step.Configuration is not LlmCallConfig config)
        {
            return new StepExecutionResult
            {
                Status = StepExecutionStatus.Failed,
                Duration = TimeSpan.Zero,
                ErrorMessage = $"Step '{step.Name}' has invalid configuration type for LlmCall executor."
            };
        }

        var sw = Stopwatch.StartNew();

        var userMessages = BuildUserMessages(config, upstreamOutputs);

        var command = new RunConversationCommand
        {
            AgentName = config.ModelDeploymentKey,
            SystemPrompt = config.SystemPrompt,
            UserMessages = userMessages,
            MaxTurns = 1,
            OnProgress = async progress =>
            {
                _logger.LogDebug("LLM turn {Turn} for step {Step}: {Status}",
                    progress.TurnNumber, step.Name, progress.Status);
                await Task.CompletedTask;
            }
        };

        await _notifier.NotifyStepStartedAsync(
            _executionContext.CurrentPlanId ?? new PlanId(Guid.Empty), step.Id, step.Name, StepType.LlmCall, ct);

        var result = await _sender.Send(command, ct);
        sw.Stop();

        if (result.Success)
        {
            return new StepExecutionResult
            {
                Status = StepExecutionStatus.Completed,
                Output = result.FinalResponse,
                Duration = sw.Elapsed
            };
        }

        return new StepExecutionResult
        {
            Status = StepExecutionStatus.Failed,
            ErrorMessage = result.Error ?? "LLM call failed without error details.",
            Duration = sw.Elapsed
        };
    }

    private static IReadOnlyList<string> BuildUserMessages(
        LlmCallConfig config,
        IReadOnlyDictionary<PlanStepId, string> upstreamOutputs)
    {
        var messages = new List<string>();

        foreach (var (_, output) in upstreamOutputs)
        {
            if (!string.IsNullOrEmpty(output))
                messages.Add(output);
        }

        if (messages.Count == 0)
            messages.Add("Execute the configured task.");

        return messages;
    }
}
