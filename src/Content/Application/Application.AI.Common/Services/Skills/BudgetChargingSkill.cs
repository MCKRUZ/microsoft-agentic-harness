using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Telemetry.Conventions;
using Microsoft.Agents.AI;

namespace Application.AI.Common.Services.Skills;

/// <summary>
/// Wraps a framework skill so the tokens it puts into the model's context when the model pulls it on
/// demand are charged to <see cref="IContextBudgetTracker"/> — the Tier 2 body served by
/// <c>load_skill</c>, and the Tier 3 supporting files served by <c>read_skill_resource</c>.
/// </summary>
/// <remarks>
/// <para>
/// Without this, those pulls happen entirely inside the framework's <see cref="AgentSkillsProvider"/>.
/// The harness records the static system prompt and the tool schemas at agent construction and then sees
/// nothing further, so a turn that loads several large skills mid-flight spends context the tracker still
/// believes is free. The budget then under-reports on precisely the turns most likely to exhaust it
/// (issue #248).
/// </para>
/// <para>
/// <b>Why a wrapper and not a skills-source decorator.</b> The framework's source abstraction hands over
/// the skill <em>list</em>; the content pull happens later, per skill. Wrapping the source would give
/// exact accounting of nothing. This sits where the content actually flows. It also cannot be a subclass
/// of <see cref="AgentInlineSkill"/> — that type is sealed — so it derives from the <see cref="AgentSkill"/>
/// base and forwards every member.
/// </para>
/// <para>
/// <b>Charged per pull, not per skill.</b> Each <c>load_skill</c> call appends its own tool-result message
/// to the conversation, so a skill loaded twice really does cost twice. Deduplicating would restore the
/// under-reporting this exists to remove.
/// </para>
/// <para>
/// This records consumption; it does not refuse it. <see cref="IContextBudgetTracker.EnsureBudget"/> is
/// deliberately not called here — turning an over-budget skill load into a thrown exception mid-turn is a
/// governance decision that belongs to whoever owns the turn, not to an accounting wrapper.
/// </para>
/// <para>
/// Instances are shared: the skill is held by a provider attached to an agent that is cached across turns
/// and may serve concurrent ones. All state here is the injected collaborators, and
/// <see cref="IContextBudgetTracker"/> is itself thread-safe.
/// </para>
/// </remarks>
public sealed class BudgetChargingSkill : AgentSkill
{
    /// <summary>Budget component recording skill bodies served through <c>load_skill</c>.</summary>
    public const string Tier2Component = "skills_tier2";

    /// <summary>Budget component recording supporting files served through <c>read_skill_resource</c>.</summary>
    public const string Tier3Component = "skills_tier3";

    private readonly AgentSkill _inner;
    private readonly string _agentName;
    private readonly IContextBudgetTracker _budgetTracker;

    /// <summary>
    /// Initialises a wrapper that charges <paramref name="inner"/>'s on-demand pulls to
    /// <paramref name="agentName"/>'s budget.
    /// </summary>
    /// <param name="inner">The skill whose content pulls are being accounted for.</param>
    /// <param name="agentName">
    /// The agent whose budget is charged. Must be the same name the rest of the context accounting uses,
    /// or the skill tokens land in a budget nobody reads.
    /// </param>
    /// <param name="budgetTracker">Receives the measured allocations.</param>
    public BudgetChargingSkill(AgentSkill inner, string agentName, IContextBudgetTracker budgetTracker)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentNullException.ThrowIfNull(budgetTracker);

        _inner = inner;
        _agentName = agentName;
        _budgetTracker = budgetTracker;
    }

    /// <summary>The wrapped skill's frontmatter, unchanged — this wrapper alters no Tier 1 content.</summary>
    public override AgentSkillFrontmatter Frontmatter => _inner.Frontmatter;

    /// <summary>
    /// Serves the skill body (Tier 2) and charges what it measures to the agent's budget.
    /// </summary>
    /// <param name="cancellationToken">Cancels the underlying pull.</param>
    /// <returns>The wrapped skill's content, byte for byte.</returns>
    public override async ValueTask<string> GetContentAsync(CancellationToken cancellationToken = default)
    {
        var content = await _inner.GetContentAsync(cancellationToken).ConfigureAwait(false);
        Charge(Tier2Component, ContextConventions.SkillsTierValues.Folder, content);
        return content;
    }

    /// <summary>
    /// Resolves a supporting file (Tier 3), wrapping it so the charge lands when its content is actually
    /// read rather than when it is merely resolved.
    /// </summary>
    /// <param name="name">The resource name the model asked for.</param>
    /// <param name="cancellationToken">Cancels the underlying lookup.</param>
    /// <returns>
    /// A charging wrapper over the resolved resource, or <see langword="null"/> when the wrapped skill owns
    /// no resource by that name — a miss costs the model nothing, so it must cost the budget nothing.
    /// </returns>
    /// <remarks>
    /// This returns a descriptor, not content: the framework calls
    /// <see cref="AgentSkillResource.ReadAsync"/> on it afterwards. Charging here would bill for a file the
    /// model may never receive, and would miss the size of the one it does.
    /// </remarks>
    public override async ValueTask<AgentSkillResource?> GetResourceAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var resource = await _inner.GetResourceAsync(name, cancellationToken).ConfigureAwait(false);
        return resource is null ? null : new ChargingResource(resource, this);
    }

    /// <summary>
    /// Forwards a script lookup unchanged.
    /// </summary>
    /// <param name="name">The script name the model asked for.</param>
    /// <param name="cancellationToken">Cancels the underlying lookup.</param>
    /// <returns>The wrapped skill's script, or <see langword="null"/>.</returns>
    /// <remarks>
    /// Deliberately not charged. This returns the script definition, not its output — what would enter the
    /// context is whatever the script produces when run, and the harness runs skill scripts through its own
    /// sandboxed tool chain rather than the framework's runner, so that output is accounted for there. On
    /// the production path no scripts are registered at all and this always returns <see langword="null"/>.
    /// </remarks>
    public override ValueTask<AgentSkillScript?> GetScriptAsync(
        string name,
        CancellationToken cancellationToken = default)
        => _inner.GetScriptAsync(name, cancellationToken);

    /// <summary>
    /// Records <paramref name="text"/>'s estimated token cost against the agent's budget and emits the
    /// matching context-source telemetry.
    /// </summary>
    /// <param name="component">The budget component to charge.</param>
    /// <param name="tier">The disclosure tier, for the metric dimension.</param>
    /// <param name="text">The content that reached the model.</param>
    private void Charge(string component, string tier, string? text)
    {
        var tokens = TokenEstimationHelper.EstimateTokens(text);
        if (tokens == 0)
            return;

        _budgetTracker.RecordAllocation(_agentName, component, tokens);

        ContextBudgetMetrics.SkillsLoadedTokens.Record(tokens,
            new KeyValuePair<string, object?>(AgentConventions.Name, _agentName),
            new KeyValuePair<string, object?>(ContextConventions.SkillsTier, tier));
        ContextSourceMetrics.SourceTokens.Record(tokens,
            new KeyValuePair<string, object?>(ContextConventions.SourceType, ContextConventions.SourceTypeValues.Skills),
            new KeyValuePair<string, object?>(AgentConventions.Name, _agentName));
    }

    /// <summary>
    /// A resource descriptor that charges its content to the owning skill's budget as it is read.
    /// </summary>
    /// <param name="inner">The resolved resource whose read is being accounted for.</param>
    /// <param name="owner">The skill whose budget the read is charged to.</param>
    private sealed class ChargingResource(AgentSkillResource inner, BudgetChargingSkill owner)
        : AgentSkillResource(inner.Name, inner.Description)
    {
        /// <summary>
        /// Reads the wrapped resource and charges what it returns.
        /// </summary>
        /// <param name="serviceProvider">Passed through to the wrapped resource.</param>
        /// <param name="cancellationToken">Cancels the underlying read.</param>
        /// <returns>The wrapped resource's value, unchanged.</returns>
        /// <remarks>
        /// The framework serialises this value on its way to the model, so measuring its string form is
        /// the closest honest estimate available without re-doing that serialisation.
        /// </remarks>
        public override async Task<object?> ReadAsync(
            IServiceProvider? serviceProvider = null,
            CancellationToken cancellationToken = default)
        {
            var value = await inner.ReadAsync(serviceProvider, cancellationToken).ConfigureAwait(false);
            owner.Charge(Tier3Component, ContextConventions.SkillsTierValues.FilingCabinet, value?.ToString());
            return value;
        }
    }
}
