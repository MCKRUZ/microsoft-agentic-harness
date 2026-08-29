using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Factories;

// Context-provider half of AgentExecutionContextFactory: builds the AIContextProvider rail an agent
// runs on — progressive skill disclosure, the tool permission filter, cross-session and learnings
// recall, the governance wrapper, and the per-turn budget measurer.
//
// ORDER ON THIS RAIL IS BEHAVIOUR, NOT STYLE. The runtime feeds each provider the previous one's
// output, so a provider only sees what everything above it produced. Two positional rules hold the
// rail together, and each is stated once where it is enforced rather than restated here:
// tool contributors go above ToolPermissionFilter (see the inline comment at that call site), and the
// per-turn measurer goes last (see AppendPerTurnBudgetProvider). Moving a line here changes which
// tools an agent can call and what its budget records. AgentExecutionContextFactoryRailOrderTests
// asserts both by position rather than by presence, because every other test in this assembly reads
// the rail with OfType<T>().Single() and so passes whatever the order is.
//
// Deliberately a plain comment, not an XML doc: the type's <summary> lives on the primary partial in
// AgentExecutionContextFactory.cs. A second class-level <summary> on the same type would be merged
// into one <member> entry, and which one tooling shows is compile-order dependent.
public partial class AgentExecutionContextFactory
{
    /// <summary>
    /// What the agent was already charged for before its rail ran — the baseline the per-turn measurer
    /// subtracts so a turn is billed for what the rail added rather than for the prompt again.
    /// </summary>
    /// <param name="AgentName">The agent whose budget the per-turn context is charged to.</param>
    /// <param name="Instruction">The static system prompt, already charged separately.</param>
    /// <param name="ToolCount">The number of tools the agent was built with, already charged as schemas.</param>
    /// <remarks>
    /// One concept rather than three parameters travelling together through two signatures. The three
    /// are only ever read as a set, and naming the set is what makes it obvious that a fourth thing the
    /// measurer needs belongs here rather than as another argument.
    /// </remarks>
    private readonly record struct PerTurnBudgetBaseline(string AgentName, string Instruction, int ToolCount);

    /// <summary>
    /// Unions the context providers for this agent over <paramref name="disclosableSkills"/>, which the
    /// caller built once so this wiring and the prompt's disclosure decision cannot disagree.
    /// The <paramref name="effectiveAllowlist"/> (the skills' combined constraint already capped by any
    /// agent tool ceiling) drives a single <see cref="Services.Agent.ToolPermissionFilter"/>. It is
    /// <see langword="null"/> when no restriction is active (no filter is wired), or a concrete set —
    /// possibly empty, meaning deny-all — when a restriction applies.
    /// </summary>
    /// <param name="skillCount">How many skills this agent was built from; diagnostics only.</param>
    /// <param name="effectiveAllowlist">The one allowlist governing this agent, or <see langword="null"/>.</param>
    /// <param name="disclosableSkills">The skills the framework provider is given, built once by the caller.</param>
    /// <param name="baseline">What the agent was already charged for; see <see cref="PerTurnBudgetBaseline"/>.</param>
    /// <param name="owningAgentId">
    /// This agent's own id, for <see cref="Services.Agent.PeerAgentContextProvider"/>'s self-exclusion
    /// (#518) — <see langword="null"/> for a bare-skill agent with no owning <c>AGENT.md</c>.
    /// </param>
    /// <returns>
    /// The ordered rail as a read-only list. Never empty: the governance wrapper is on every agent's
    /// rail (see the attachment site below), so there is no such thing as an agent with no providers.
    /// </returns>
    /// <remarks>
    /// The rail is returned read-only, so appending to it throws rather than silently displacing the
    /// per-turn measurer from the last position — see <see cref="AppendPerTurnBudgetProvider"/> for why
    /// that position is load-bearing (issue #277). The rail leaves this method through a settable
    /// <see cref="IList{T}"/> property on the execution context and is handed to the framework as one,
    /// which is why the guard has to be the instance's own behaviour: no type on that path can express
    /// "this list is finished".
    /// </remarks>
    private IList<AIContextProvider> BuildMergedAIContextProviders(
        int skillCount,
        IReadOnlyList<string>? effectiveAllowlist,
        IReadOnlyList<DisclosableSkill> disclosableSkills,
        PerTurnBudgetBaseline baseline,
        string? owningAgentId)
    {
        var providers = new List<AIContextProvider>();

        // Peer-agent descriptions (#518). No config gate — unlike the two recall providers below,
        // this is not an optional feature: it is what makes the Agents lane correct. Placed early,
        // ahead of the governance wrapper and the per-turn measurer, so its contribution is governed
        // and charged like everything else on the rail. Contributes nothing (empty AIContext) when
        // this agent has zero delegatable peers, so a host with one agent pays nothing extra.
        providers.Add(new Services.Agent.PeerAgentContextProvider(_agentRegistry, owningAgentId));

        if (disclosableSkills.Count > 0)
        {
            // Registering the agent's own skills, rather than a directory to search, is what keeps
            // load_skill from advertising skills this agent was never assigned.
            providers.Add(new AgentSkillsProviderBuilder()
                .UseSkills(disclosableSkills.Select(s => s.Skill))
                .UseOptions(SkillDisclosureDefaults.Configure)
                .Build());

            _logger.LogDebug("Wired AgentSkillsProvider with {SkillCount} skill(s)", disclosableSkills.Count);
        }

        // Placed immediately after the skills provider so it sees the framework's disclosure tools. Note
        // what this position does and does not guarantee: the framework feeds each provider the previous
        // one's output, so the filter's removals survive into everything added below. But a provider added
        // *after* this line whose own contribution introduces a tool would introduce it unfiltered — the
        // filter has already run. Any future tool-contributing provider belongs above this line.
        if (effectiveAllowlist is not null)
        {
            providers.Add(new Services.Agent.ToolPermissionFilter(effectiveAllowlist));

            _logger.LogDebug("Wired ToolPermissionFilter with {Count} allowed tool(s) for {SkillCount} skill(s)",
                effectiveAllowlist.Count, skillCount);
        }

        // Cross-session memory recall, then task-similarity learnings recall. Both resolve their
        // tenant-aware dependency per invocation from the current request scope, so both are safe to
        // attach to a singleton-cached agent; the learnings one injects the most task-relevant lessons
        // at turn start, which is the read half of the self-improving loop.
        if (_appConfig.CurrentValue.AI?.KnowledgeBridge?.Enabled == true)
        {
            AddRecallProvider(
                providers,
                scope => new Services.Agent.KnowledgeMemoryContextProvider(
                    scope, _appConfig,
                    _loggerFactory.CreateLogger<Services.Agent.KnowledgeMemoryContextProvider>()),
                "cross-session recall");
        }

        if (_appConfig.CurrentValue.AI?.LearningsRecall?.Enabled == true)
        {
            AddRecallProvider(
                providers,
                scope => new Services.Agent.LearningsRecallContextProvider(
                    scope, _appConfig,
                    _loggerFactory.CreateLogger<Services.Agent.LearningsRecallContextProvider>()),
                "task-similarity recall");
        }

        // Governance wrapper — added after the recall providers so it wraps the final, filtered tool
        // set, guaranteeing the governor gates every tool the agent can call including the framework
        // progressive-disclosure tools that bypass ToolChainBuilder.
        //
        // ATTACHED UNCONDITIONALLY, AND THAT IS THE FIX, NOT AN OVERSIGHT (issue #347). Whether
        // governance is on cannot be decided here: this rail is built once, at agent construction, and
        // agents are cached — while a bundle run arms enforcement later and per run, through the ambient
        // capability envelope that GovernanceEnforcement.IsActive reads. Any condition evaluated at this
        // point therefore answers for the wrong moment, and the one that used to be here (the global
        // switch alone) published a bundle's disclosure tools unwrapped on the default composition. The
        // provider is transparent unless an admission chain is ambient for the turn, so what an
        // ungoverned host pays is one list entry plus, on an agent that has skills, a bounded re-wrap of
        // the three framework disclosure tools once per turn — orders of magnitude under the LLM call it
        // rides along with.
        providers.Add(new Services.Agent.GoverningToolContextProvider(
            _loggerFactory.CreateLogger<Services.Agent.GoverningToolContextProvider>(),
            _sanitizer));

        AppendPerTurnBudgetProvider(providers, baseline);

        // AsReadOnly wraps rather than copies, so the returned list is a live view of a list nothing
        // else holds a reference to — the local goes out of scope here.
        return providers.AsReadOnly();
    }

    /// <summary>
    /// Adds one of the two recall providers, when a request scope exists for it to read identity from.
    /// </summary>
    /// <param name="providers">The rail under construction.</param>
    /// <param name="create">Builds the provider from the resolved ambient scope.</param>
    /// <param name="purpose">What this provider recalls; diagnostics only.</param>
    /// <remarks>
    /// <para>
    /// The two recall providers are the same steps with the type swapped — resolve the ambient scope,
    /// skip when absent, construct, log — and were written out twice.
    /// </para>
    /// <para>
    /// <strong>The feature flag is checked by the caller, not here, and must stay that way.</strong>
    /// Both flags default to disabled, so on the default path nothing is called: no service lookup for
    /// <see cref="IAmbientRequestScope"/>, and not even a delegate allocated for <paramref name="create"/>.
    /// Taking the flag as a parameter would move both costs onto every agent construction, for a feature
    /// almost nobody has switched on.
    /// </para>
    /// <para>
    /// A missing <see cref="IAmbientRequestScope"/> is a silent skip rather than a failure because the
    /// provider cannot function without one and the feature is optional — a host that enabled recall
    /// without registering the scope gets no recall, not a broken agent.
    /// </para>
    /// </remarks>
    private void AddRecallProvider(
        List<AIContextProvider> providers,
        Func<IAmbientRequestScope, AIContextProvider> create,
        string purpose)
    {
        var ambientScope = _serviceProvider.GetService<IAmbientRequestScope>();
        if (ambientScope is null)
            return;

        var provider = create(ambientScope);
        providers.Add(provider);

        _logger.LogDebug("Wired {ProviderType} for {Purpose}", provider.GetType().Name, purpose);
    }

    /// <summary>
    /// Appends the measurer that charges whatever <paramref name="providers"/> inject into each turn to
    /// the budget of the agent named by <paramref name="baseline"/>.
    /// </summary>
    /// <param name="providers">The rail built for this agent, mutated in place.</param>
    /// <param name="baseline">What the agent was already charged for, which the measurer subtracts.</param>
    /// <remarks>
    /// <strong>This is the canonical statement of the lastness rule.</strong> The measurer must be last:
    /// the runtime feeds each provider the previous one's output, so only the final position sees
    /// everything the others contributed. That is why it is appended from the tail of
    /// <see cref="BuildMergedAIContextProviders"/> rather than by whoever consumes the rail — lastness is
    /// then a property of the builder, not of a call sequence somewhere else — and why that method hands
    /// the finished rail out read-only. Placing it after
    /// <see cref="Services.Agent.GoverningToolContextProvider"/>, which is itself documented as going
    /// last, is safe: this provider neither adds nor removes tools, so it cannot escape that governance
    /// wrapper or defeat it. Nothing is appended when no budget tracker is wired in, leaving a host that
    /// does not track context with exactly the rail it had before.
    /// </remarks>
    private void AppendPerTurnBudgetProvider(
        List<AIContextProvider> providers,
        PerTurnBudgetBaseline baseline)
    {
        if (_budgetTracker is null || providers.Count == 0)
            return;

        providers.Add(new Services.Agent.PerTurnBudgetContextProvider(
            baseline.AgentName,
            _budgetTracker,
            baseline.Instruction,
            baseline.ToolCount,
            _loggerFactory.CreateLogger<Services.Agent.PerTurnBudgetContextProvider>()));

        _logger.LogDebug(
            "Wired PerTurnBudgetContextProvider for {AgentName} behind {ProviderCount} context provider(s)",
            baseline.AgentName, providers.Count - 1);
    }
}
