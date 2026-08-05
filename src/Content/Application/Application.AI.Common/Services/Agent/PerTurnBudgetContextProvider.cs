using Application.AI.Common.Extensions;
using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Telemetry.Conventions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Agent;

/// <summary>
/// Charges the context every other <see cref="AIContextProvider"/> injects into a turn to
/// <see cref="IContextBudgetTracker"/> — the skills index card and the framework's disclosure tools,
/// cross-session memory recall, and task-similarity learnings.
/// </summary>
/// <remarks>
/// <para>
/// Without this the budget records only what is known when the agent is built: the static system prompt
/// and the harness's own tool schemas. Everything contributed on the provider rail is paid on <em>every
/// turn</em> and counted on none, so the gap between what the budget reports and what the model's context
/// actually holds widens as a conversation runs — fastest on the agents with the most memory to recall and
/// the most skills to advertise (issue #266).
/// </para>
/// <para>
/// <b>Why one measurer at the end of the rail, and not a wrapper per provider.</b> A per-provider wrapper
/// would give a finer breakdown, and it cannot be built: the runtime rejects an agent whose providers do
/// not all carry distinct <see cref="AIContextProvider.StateKeys"/>, and every instance of one wrapper type
/// shares the default key of that type, so the agent's constructor throws. Even setting that aside, a
/// delegating provider would have to forward every member of the base — the post-turn notification, the
/// three message filters, the state keys, service resolution — and forgetting any one of them silently
/// disables the provider it wraps. This assembly has already shipped that class of defect four times.
/// Sitting at the end of the chain needs none of it.
/// </para>
/// <para>
/// <b>How it measures.</b> The runtime feeds each provider the accumulated output of the ones before it,
/// starting from the agent's own instructions and tools. Registered last, this provider therefore receives
/// the finished context, and what the rail added is the difference between that and the baseline it was
/// given at construction. It contributes nothing of its own, so it is trivially additive and cannot
/// disturb what it measures.
/// </para>
/// <para>
/// <b>Charged every turn, on purpose.</b> The system prompt and tool schemas are charged once, when the
/// agent is built. This cost genuinely recurs — the index card and every recalled block are re-sent with
/// each request — so charging it once would reproduce the under-reporting this exists to remove.
/// </para>
/// <para>
/// <b>What "measured" means here.</b> Instruction text is captured exactly and converted with the shared
/// <see cref="TokenEstimationHelper"/>, the same characters-per-token approximation the system prompt and
/// tool schemas are charged with, so every component of the budget is stated in one unit. Tools contributed
/// on the rail are charged at the shared flat per-schema estimate, because their serialised schemas are
/// built by the model client and never pass through the harness.
/// </para>
/// <para>
/// <b>What it does not cover.</b> Only <see cref="AIContext.Instructions"/> and
/// <see cref="AIContext.Tools"/> are charged. A provider that contributed
/// <see cref="AIContext.Messages"/> instead would go uncounted: unlike the instructions, whose baseline is
/// the agent's fixed prompt, the message baseline is the conversation itself and grows every turn, so the
/// end of the rail cannot tell an injected message from the turn's own. None of the providers wired today
/// contributes messages. A future one that does needs a baseline captured at the head of the rail rather
/// than passed in at construction.
/// </para>
/// <para>
/// This records consumption; it does not refuse it. <see cref="IContextBudgetTracker.EnsureBudget"/> is
/// deliberately not called — turning an over-budget turn into a thrown exception is a governance decision
/// belonging to whoever owns the turn, not to an accounting provider.
/// </para>
/// <para>
/// Instances are shared: the provider is attached to an agent cached across turns that may serve concurrent
/// ones. All state is the injected collaborators and the immutable baseline, and
/// <see cref="IContextBudgetTracker"/> is itself thread-safe.
/// </para>
/// </remarks>
public sealed class PerTurnBudgetContextProvider : AIContextProvider
{
    private readonly string _agentName;
    private readonly IContextBudgetTracker _budgetTracker;
    private readonly string _baselineInstructions;
    private readonly int _baselineToolCount;
    private readonly ILogger<PerTurnBudgetContextProvider> _logger;

    /// <summary>
    /// Initialises a measurer for one agent.
    /// </summary>
    /// <param name="agentName">
    /// The agent whose budget is charged. Must be the same name the rest of the context accounting uses,
    /// or these tokens land in a budget nobody reads.
    /// </param>
    /// <param name="budgetTracker">Receives the measured allocations.</param>
    /// <param name="baselineInstructions">
    /// The agent's static instructions — what the rail starts from, already charged as the system prompt.
    /// Everything beyond it is what the rail added.
    /// </param>
    /// <param name="baselineToolCount">
    /// The number of tools the agent was built with, already charged as tool schemas. Tools beyond this
    /// count were contributed on the rail.
    /// </param>
    /// <param name="logger">Receives the diagnostic when the measured shape is not the expected one.</param>
    public PerTurnBudgetContextProvider(
        string agentName,
        IContextBudgetTracker budgetTracker,
        string? baselineInstructions,
        int baselineToolCount,
        ILogger<PerTurnBudgetContextProvider> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentNullException.ThrowIfNull(budgetTracker);
        ArgumentNullException.ThrowIfNull(logger);

        _agentName = agentName;
        _budgetTracker = budgetTracker;
        _baselineInstructions = baselineInstructions ?? string.Empty;
        _baselineToolCount = baselineToolCount;
        _logger = logger;
    }

    /// <summary>
    /// Charges what the providers ahead of this one put into the turn, and adds nothing.
    /// </summary>
    /// <param name="context">The accumulated context, as it will be sent to the model.</param>
    /// <param name="cancellationToken">Unused — measuring allocates no I/O.</param>
    /// <returns>An empty contribution.</returns>
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        Charge(context.AIContext);

        // A fresh instance per turn rather than a shared empty one: AIContext's properties are settable,
        // and handing the same object to the framework on every turn of every agent would make one
        // mutation anywhere a cross-agent bug. Allocating an empty record costs nothing worth saving.
        return ValueTask.FromResult(new AIContext());
    }

    /// <summary>
    /// Records the difference between <paramref name="accumulated"/> and the construction-time baseline.
    /// </summary>
    /// <param name="accumulated">The context as assembled by the providers ahead of this one.</param>
    private void Charge(AIContext accumulated)
    {
        var tokens = EstimateInjectedInstructionTokens(accumulated.Instructions)
            + TokenEstimationHelper.EstimateToolSchemaTokens(
                (accumulated.Tools?.Count() ?? 0) - _baselineToolCount);

        if (tokens == 0)
            return;

        _budgetTracker.RecordAndPublish(
            _agentName,
            ContextConventions.BudgetComponents.PerTurnContext,
            ContextConventions.SourceTypeValues.PerTurnContext,
            tokens,
            ContextBudgetMetrics.PerTurnContextTokens);
    }

    /// <summary>
    /// Estimates the tokens the rail added to the instructions.
    /// </summary>
    /// <param name="accumulated">The instructions as assembled by the providers ahead of this one.</param>
    /// <returns>The estimated token count of the added text, or 0 when nothing was added.</returns>
    /// <remarks>
    /// The base merge appends each provider's contribution to what it was given, so the accumulated
    /// instructions begin with the baseline and the tail is what the rail contributed. A provider that
    /// rewrote the text instead of appending to it would break that assumption and is logged, because the
    /// resulting figure is then only a best effort — and because rewriting on this hook is itself a defect
    /// worth surfacing.
    /// </remarks>
    private int EstimateInjectedInstructionTokens(string? accumulated)
    {
        var text = accumulated ?? string.Empty;
        var injectedChars = text.Length - _baselineInstructions.Length;

        if (injectedChars <= 0)
            return 0;

        // Gated: the comparison scans the whole system prompt on every turn to detect a condition the
        // merge contract makes essentially impossible, and its only outcome is the warning below.
        if (_logger.IsEnabled(LogLevel.Warning)
            && _baselineInstructions.Length > 0
            && !text.StartsWith(_baselineInstructions, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Per-turn context for agent {AgentName} does not begin with the agent's own instructions; " +
                "a context provider rewrote them rather than appending. The charge is a best-effort estimate.",
                _agentName);
        }

        // Sliced as a span, not a substring: the tail is everything the rail injected — index card plus
        // every recalled block — and copying it only to read its length would allocate kilobytes per turn.
        return TokenEstimationHelper.EstimateTokens(text.AsSpan(text.Length - injectedChars));
    }
}
