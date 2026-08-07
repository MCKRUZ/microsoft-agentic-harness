using System.Diagnostics;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Services.Governance;
using Application.AI.Common.Interfaces.Planner;
using Application.Core.CQRS.Agents.RunConversation;
using Domain.AI.Governance;
using Domain.AI.Planner;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
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
/// <strong>Two identities, deliberately separate — see <see cref="PlanRunKeys"/>.</strong> Each step
/// gets its <em>own</em> conversation id. A conversation id is not just a budget key: it is the sole
/// key of <c>IAgentConversationCache</c>, which returns a cached agent on a hit and ignores the
/// requested skills and options, so sharing one id across steps would make a step run under another
/// step's agent (with <c>MaxParallelSteps</c> defaulting to 10, concurrent steps are the normal case)
/// and would let the first step to finish evict the cache and clear skill tracking for steps still
/// running. Cross-step spend is instead accumulated against a separate run-level budget key.
/// </para>
/// <para>
/// <strong>Each step's conversation runs in its own DI scope.</strong> The conversation is dispatched
/// through an <see cref="ISender"/> resolved from a fresh scope rather than the plan's scope, because
/// <c>IAgentExecutionContext</c> is scoped and single-binding: <c>AgentContextPropagationBehavior</c>
/// calls <c>Initialize</c> for the nested agent-turn request, whose agent id is the step's
/// <em>deployment key</em> and whose conversation id is this step's. The plan's scope is already bound
/// by <c>PlanRunExecutor</c> to the caller's identity and the run's conversation, and re-initializing
/// one context with a different agent or conversation throws by design (it is normally a scope leak).
/// Dispatching in a per-step scope gives every turn a clean context to bind, which is also what makes
/// the per-step cache and skill-tracking isolation real rather than nominal. This mirrors
/// <c>BundleRunExecutor</c>, which resolves its mediator from its own scope for the same reason. The
/// capability envelope is ambient (<c>AsyncLocal</c>) and flows into the child scope unchanged, so
/// confinement is unaffected.
/// </para>
/// <para>
/// <strong>Budget ownership.</strong> <c>RunConversationCommandHandler</c> owns its conversation's
/// budget entry and <c>Release</c>s it in a <c>finally</c>, so an entry under any conversation id is
/// erased when that conversation ends and can never carry spend to the next step. Rather than fight
/// that ownership, the plan run accumulates <see cref="ConversationResult.TotalTokens"/> against its
/// own <see cref="PlanRunKeys.RunBudgetKey"/> after each step and gates the next step on it;
/// <c>PlanRunExecutor</c> releases that key when the run ends. With no run scope (an ad-hoc direct
/// in-process call) there is no run-level budget and behavior is exactly as before.
/// </para>
/// </remarks>
public sealed class LlmCallStepExecutor : IPlanStepExecutor
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPlanProgressNotifier _notifier;
    private readonly IToolInvocationGovernor _toolInvocationGovernor;
    private readonly IToolCallObserverChain _observers;
    private readonly IConversationBudgetTracker _conversationBudget;
    private readonly IAgentExecutionContext _agentContext;
    private readonly PlanExecutionContext _executionContext;
    private readonly ILogger<LlmCallStepExecutor> _logger;

    /// <summary>Initializes a new instance of the <see cref="LlmCallStepExecutor"/> class.</summary>
    /// <param name="scopeFactory">Creates the per-step scope the conversation is dispatched in.</param>
    /// <param name="notifier">Plan progress notifier.</param>
    /// <param name="toolInvocationGovernor">Authorizes inference against the ambient capability envelope.</param>
    /// <param name="observers">The host's own tool-call rules, consulted after the governor allows the step.</param>
    /// <param name="conversationBudget">Lifetime token budget shared across the plan run's inference.</param>
    /// <param name="agentContext">Supplies the run's conversation identity for budget keying.</param>
    /// <param name="executionContext">Current plan execution context.</param>
    /// <param name="logger">Structured logger.</param>
    public LlmCallStepExecutor(
        IServiceScopeFactory scopeFactory,
        IPlanProgressNotifier notifier,
        IToolInvocationGovernor toolInvocationGovernor,
        IToolCallObserverChain observers,
        IConversationBudgetTracker conversationBudget,
        IAgentExecutionContext agentContext,
        PlanExecutionContext executionContext,
        ILogger<LlmCallStepExecutor> logger)
    {
        _scopeFactory = scopeFactory;
        _notifier = notifier;
        _toolInvocationGovernor = toolInvocationGovernor;
        _observers = observers;
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

        var runScope = ResolveRunScope();
        var runBudgetKey = runScope is null ? null : PlanRunKeys.RunBudgetKey(runScope);

        if (runBudgetKey is not null
            && (await _conversationBudget.GetStatusAsync(runBudgetKey, ct)).IsExhausted)
        {
            _logger.LogWarning(
                "LlmCall step {Step} refused: plan run {RunScope} has exhausted its lifetime token budget",
                step.Name, runScope);
            return new StepExecutionResult
            {
                Status = StepExecutionStatus.Failed,
                Duration = TimeSpan.Zero,
                ErrorMessage = PlanStepErrors.BudgetExhausted,
                IsPolicyDenial = true
            };
        }

        var sw = Stopwatch.StartNew();

        // Per-step, never shared: this id keys the agent cache, skill-completion tracking, and the
        // observability session. See PlanRunKeys. Held in a local because this executor owns its whole
        // lifetime — one command, MaxTurns 1 — and so is the only place that can release the budget
        // entry the handler accrues under it.
        var stepConversationId = runScope is null
            ? Guid.NewGuid().ToString()
            : PlanRunKeys.StepConversationId(runScope, step.Id);

        var command = new RunConversationCommand
        {
            AgentName = config.ModelDeploymentKey,
            SystemPrompt = config.SystemPrompt,
            UserMessages = BuildUserMessages(config, upstreamOutputs),
            MaxTurns = 1,
            ConversationId = stepConversationId,
            OnProgress = async progress =>
            {
                _logger.LogDebug("LLM turn {Turn} for step {Step}: {Status}",
                    progress.TurnNumber, step.Name, progress.Status);
                await Task.CompletedTask;
            }
        };

        // Per-step scope: the turn binds its own IAgentExecutionContext (deployment key + this step's
        // conversation) without colliding with the run identity the plan's scope already holds.
        ConversationResult result;
        try
        {
            await using var stepScope = _scopeFactory.CreateAsyncScope();
            var sender = stepScope.ServiceProvider.GetRequiredService<ISender>();
            result = await sender.Send(command, ct);
        }
        finally
        {
            // Release the step's own budget entry on every exit path, including a throwing turn. The
            // command handler no longer releases anything — a conversation there outlives one run and
            // one host (issue #235) — but a step conversation genuinely does not: it was created here,
            // used for exactly one turn, and is never resumed. Left unreleased it would leave one
            // abandoned entry per step of every plan run. Uses None so a cancelled step still cleans up.
            await _conversationBudget.ReleaseAsync(stepConversationId, CancellationToken.None);
        }

        sw.Stop();

        // Fold this step's spend into the run-level budget the plan owns. Accounted separately rather
        // than read back from the step's own entry because that entry is per-step by construction —
        // summing the run would mean enumerating keys the tracker does not expose.
        if (runBudgetKey is not null && result.TotalTokens > 0)
            await _conversationBudget.RecordUsageAsync(runBudgetKey, result.TotalTokens, ct);

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
        var decision = await _toolInvocationGovernor
            .AuthorizeWithObserversAsync(_observers, PlanCapabilities.LlmCall, arguments: null, ct);
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
    /// Identity of the enclosing run, used to derive both the per-step conversation id and the
    /// run-level budget key — or null when this call belongs to no armed run, in which case there is
    /// no run-level budget and the step gets a throwaway conversation id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Only an explicitly armed run scope counts.</strong> The scope is read solely from the
    /// ambient <see cref="IAgentExecutionContext.ConversationId"/>, which only <c>PlanRunExecutor</c>
    /// sets. It deliberately does <em>not</em> fall back to the current plan id: that fallback would
    /// create a <c>planrun:</c> budget entry on the ungoverned in-process path, where nothing exists to
    /// release it. Because the budget tracker is a singleton and an exhausted budget is a
    /// <em>terminal, non-retryable</em> policy denial, an orphaned entry would make a plan id
    /// permanently un-runnable in-process after one exhaustion — bounded eviction caps the memory, not
    /// the semantics. Keying only on the armed scope makes creation and release symmetric: the one
    /// component that establishes a run scope is the one that releases its key.
    /// </para>
    /// <para>
    /// <strong>Invariant this executor depends on: it may only run under a conversation id armed by
    /// <c>PlanRunExecutor</c>.</strong> The <c>planrun:</c> budget key is derived from <em>any</em>
    /// ambient <see cref="IAgentExecutionContext.ConversationId"/>, but only <c>PlanRunExecutor</c>
    /// releases it. Nothing in this method distinguishes a conversation id that a run armed from one an
    /// agent turn happened to be carrying, so the two are only kept apart by where plan execution is
    /// driven from. That holds today because <c>ExecutePlanCommandHandler</c> is the sole in-process
    /// <c>IPlanExecutor</c> caller and has no production dispatcher, which is why the orphan is
    /// currently unreachable rather than merely unlikely. A future host that wires plan execution
    /// <em>inside</em> an agent turn would break it: the step would create a run budget entry keyed on
    /// the turn's conversation id that no one releases, and because budget exhaustion is a
    /// <c>IsPolicyDenial</c> — terminal and non-retryable — every subsequent LlmCall step under that
    /// conversation id would be denied permanently. Any such host must arm the run through
    /// <c>PlanRunExecutor</c> so creation and release stay paired.
    /// </para>
    /// <para>
    /// Sub-plans inherit correctly: <c>SubPlanStepExecutor</c> re-stamps the parent's conversation onto
    /// the child scope, so an enveloped run shares one budget across its whole sub-plan tree, released
    /// once when the run ends.
    /// </para>
    /// </remarks>
    private string? ResolveRunScope() =>
        string.IsNullOrEmpty(_agentContext.ConversationId) ? null : _agentContext.ConversationId;

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
