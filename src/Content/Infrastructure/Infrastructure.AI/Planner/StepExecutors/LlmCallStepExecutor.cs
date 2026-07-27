using System.Diagnostics;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Planner;
using Application.Core.CQRS.Agents.RunConversation;
using Domain.AI.Governance;
using Domain.AI.Planner;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Planner.StepExecutors;

/// <summary>
/// Executes LLM inference steps by delegating to <see cref="RunConversationCommand"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Envelope confinement.</strong> The step is authorized as the well-known capability
/// <see cref="PlanCapabilities.LlmCall"/> through <see cref="IToolInvocationGovernor"/>, the same
/// choke point the tool and retrieval steps use. Without it a caller holding the most restrictive
/// possible envelope (no tools, Restricted ceiling) could still drive unbounded model inference on
/// the host's credential with a plan-authored system prompt: tools the resulting agent calls stay
/// confined, so the exposure is spend and prompt surface rather than privilege escalation, but an
/// unmetered path defeats the point of confining the run.
/// </para>
/// <para>
/// <strong>Budget.</strong> <c>RunConversationCommandHandler</c> already enforces the lifetime
/// conversation budget — but it keys on <see cref="RunConversationCommand.ConversationId"/>, which
/// this executor previously left to its per-command <c>Guid.NewGuid()</c> default. Every step
/// therefore started a brand-new budget that could never accumulate, so a plan of N LlmCall steps
/// spent N full budgets. The ambient <see cref="IAgentExecutionContext.ConversationId"/> — set once
/// per run by <c>PlanRunExecutor</c> — is used as the key instead, so all inference in one plan run
/// shares one budget, and the step refuses to start another conversation once it is exhausted. With
/// no ambient conversation (direct in-process callers) the per-step fallback preserves today's
/// behavior.
/// </para>
/// </remarks>
public sealed class LlmCallStepExecutor : IPlanStepExecutor
{
    private readonly ISender _sender;
    private readonly IPlanProgressNotifier _notifier;
    private readonly IToolInvocationGovernor _toolInvocationGovernor;
    private readonly IConversationBudgetTracker _conversationBudget;
    private readonly IAgentExecutionContext _agentContext;
    private readonly PlanExecutionContext _executionContext;
    private readonly ILogger<LlmCallStepExecutor> _logger;

    /// <summary>Initializes a new instance of the <see cref="LlmCallStepExecutor"/> class.</summary>
    /// <param name="sender">Dispatches the conversation command.</param>
    /// <param name="notifier">Plan progress notifier.</param>
    /// <param name="toolInvocationGovernor">Authorizes inference against the ambient capability envelope.</param>
    /// <param name="conversationBudget">Lifetime token budget shared across the plan run's inference.</param>
    /// <param name="agentContext">Supplies the run's conversation identity for budget keying.</param>
    /// <param name="executionContext">Current plan execution context.</param>
    /// <param name="logger">Structured logger.</param>
    public LlmCallStepExecutor(
        ISender sender,
        IPlanProgressNotifier notifier,
        IToolInvocationGovernor toolInvocationGovernor,
        IConversationBudgetTracker conversationBudget,
        IAgentExecutionContext agentContext,
        PlanExecutionContext executionContext,
        ILogger<LlmCallStepExecutor> logger)
    {
        _sender = sender;
        _notifier = notifier;
        _toolInvocationGovernor = toolInvocationGovernor;
        _conversationBudget = conversationBudget;
        _agentContext = agentContext;
        _executionContext = executionContext;
        _logger = logger;
    }

    /// <inheritdoc />
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

        var denial = await AuthorizeAsync(step, ct);
        if (denial is not null)
            return denial;

        var conversationId = ResolveConversationId();
        if (_conversationBudget.GetStatus(conversationId).IsExhausted)
        {
            _logger.LogWarning(
                "LlmCall step {Step} refused: conversation {ConversationId} has exhausted its lifetime token budget",
                step.Name, conversationId);
            return new StepExecutionResult
            {
                Status = StepExecutionStatus.Failed,
                Duration = TimeSpan.Zero,
                ErrorMessage = PlanStepErrors.BudgetExhausted,
                IsPolicyDenial = true
            };
        }

        var sw = Stopwatch.StartNew();

        var command = new RunConversationCommand
        {
            AgentName = config.ModelDeploymentKey,
            SystemPrompt = config.SystemPrompt,
            UserMessages = BuildUserMessages(config, upstreamOutputs),
            MaxTurns = 1,
            ConversationId = conversationId,
            OnProgress = async progress =>
            {
                _logger.LogDebug("LLM turn {Turn} for step {Step}: {Status}",
                    progress.TurnNumber, step.Name, progress.Status);
                await Task.CompletedTask;
            }
        };

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

    /// <summary>
    /// Authorizes the step as <see cref="PlanCapabilities.LlmCall"/> and, when denied, produces the
    /// failed step result. Returns null when inference may proceed.
    /// </summary>
    private async Task<StepExecutionResult?> AuthorizeAsync(PlanStep step, CancellationToken ct)
    {
        var decision = await _toolInvocationGovernor.AuthorizeAsync(PlanCapabilities.LlmCall, ct);
        if (decision.IsAllowed)
            return null;

        _logger.LogWarning("LlmCall step {Step} denied by invocation governor", step.Name);
        return new StepExecutionResult
        {
            Status = StepExecutionStatus.Failed,
            Duration = TimeSpan.Zero,
            ErrorMessage = decision.DeniedMessage ?? GovernanceDenials.NotPermitted(PlanCapabilities.LlmCall),
            IsPolicyDenial = true
        };
    }

    /// <summary>
    /// The budget key for this step's inference: the run's ambient conversation id when one was armed,
    /// else the current plan id, else a fresh id. The first case is what makes the budget span the whole
    /// plan run rather than resetting per step.
    /// </summary>
    private string ResolveConversationId()
    {
        if (!string.IsNullOrEmpty(_agentContext.ConversationId))
            return _agentContext.ConversationId;

        return _executionContext.CurrentPlanId?.Value.ToString() ?? Guid.NewGuid().ToString();
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
