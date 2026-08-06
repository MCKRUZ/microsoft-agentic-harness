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
// rail together: any tool-contributing provider must go above ToolPermissionFilter (see the inline
// comment at that call site), and the per-turn measurer must go last (see AppendPerTurnBudgetProvider).
// Both are load-bearing — moving a line here changes which tools an agent can call and what its
// budget records. AgentExecutionContextFactoryRailOrderTests asserts both, by position rather than by
// presence, because every other test in this assembly reads the rail with OfType<T>().Single() and so
// passes whatever the order is.
//
// Deliberately a plain comment, not an XML doc: the type's <summary> lives on the primary partial in
// AgentExecutionContextFactory.cs. A second class-level <summary> on the same type would be merged
// into one <member> entry, and which one tooling shows is compile-order dependent.
public partial class AgentExecutionContextFactory
{
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
    /// <param name="agentName">The agent whose budget the per-turn measurer charges.</param>
    /// <param name="instruction">The static system prompt — the measurer's instruction baseline.</param>
    /// <param name="toolCount">The tool count the agent was built with — the measurer's tool baseline.</param>
    /// <returns>The ordered rail, or <see langword="null"/> when this agent needs no providers at all.</returns>
    /// <remarks>
    /// The per-turn measurer is appended here rather than by the caller, so that "it must be last" is a
    /// property of this one method instead of a property of a call sequence in another file. That closes the
    /// gap this method could close. It does not make lastness unbreakable: the rail is handed on as a
    /// mutable <see cref="IList{T}"/> through a settable property, so a consumer that appended to it would
    /// still displace the measurer. Nothing does today, and no test would catch it if something started —
    /// tracked in issue #277.
    /// </remarks>
    private IList<AIContextProvider>? BuildMergedAIContextProviders(
        int skillCount,
        IReadOnlyList<string>? effectiveAllowlist,
        IReadOnlyList<DisclosableSkill> disclosableSkills,
        string agentName,
        string instruction,
        int toolCount)
    {
        var providers = new List<AIContextProvider>();

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

        // Cross-session memory recall. The provider resolves tenant-aware IKnowledgeMemory per
        // invocation from the current request scope (via IAmbientRequestScope), so it is safe to
        // attach to a singleton-cached agent.
        if (_appConfig.CurrentValue.AI?.KnowledgeBridge?.Enabled == true)
        {
            var ambientScope = _serviceProvider.GetService<IAmbientRequestScope>();
            if (ambientScope is not null)
            {
                providers.Add(new Services.Agent.KnowledgeMemoryContextProvider(
                    ambientScope,
                    _appConfig,
                    _loggerFactory.CreateLogger<Services.Agent.KnowledgeMemoryContextProvider>()));

                _logger.LogDebug("Wired KnowledgeMemoryContextProvider for cross-session recall");
            }
        }

        // Task-similarity learnings recall. Like the memory provider above, it resolves the scoped,
        // tenant-aware ILearningRecaller per invocation from the current request scope, so it is safe to
        // attach to a singleton-cached agent. Injects the most task-relevant lessons (every source,
        // including work-memory synthesis output) at turn start — the read half of the self-improving loop.
        if (_appConfig.CurrentValue.AI?.LearningsRecall?.Enabled == true)
        {
            var ambientScope = _serviceProvider.GetService<IAmbientRequestScope>();
            if (ambientScope is not null)
            {
                providers.Add(new Services.Agent.LearningsRecallContextProvider(
                    ambientScope,
                    _appConfig,
                    _loggerFactory.CreateLogger<Services.Agent.LearningsRecallContextProvider>()));

                _logger.LogDebug("Wired LearningsRecallContextProvider for task-similarity recall");
            }
        }

        // Governance wrapper — added LAST so it wraps the final, filtered tool set. When
        // tool-invocation enforcement is on, this guarantees the governor gates every tool the agent
        // can call, including framework progressive-disclosure tools that bypass ToolChainBuilder.
        // Inert (and skipped entirely) when enforcement is off, so default behaviour is unchanged.
        if (_appConfig.CurrentValue.AI?.Governance?.EnforceToolInvocation == true)
        {
            providers.Add(new Services.Agent.GoverningToolContextProvider(
                _loggerFactory.CreateLogger<Services.Agent.GoverningToolContextProvider>()));
            _logger.LogDebug("Wired GoverningToolContextProvider (tool-invocation enforcement enabled)");
        }

        // Last, unconditionally and from inside this method — see the remarks above and on
        // AppendPerTurnBudgetProvider. An empty rail gets nothing appended, so the null below is unaffected.
        AppendPerTurnBudgetProvider(providers, agentName, instruction, toolCount);

        return providers.Count > 0 ? providers : null;
    }

    /// <summary>
    /// Appends the measurer that charges whatever <paramref name="providers"/> inject into each turn to
    /// <paramref name="agentName"/>'s budget.
    /// </summary>
    /// <param name="providers">
    /// The rail built for this agent, mutated in place. An empty rail means the agent has no providers, so
    /// there is nothing per-turn to charge and nothing is appended.
    /// </param>
    /// <param name="agentName">The agent whose budget the per-turn context is charged to.</param>
    /// <param name="instruction">
    /// The static system prompt, already charged separately. It is the baseline the measurer subtracts, so
    /// only what the rail adds is charged here rather than the prompt being billed again every turn.
    /// </param>
    /// <param name="toolCount">
    /// The number of tools the agent was built with, already charged as tool schemas — the baseline for
    /// tools the rail contributes, such as the framework's own skill-disclosure tools.
    /// </param>
    /// <remarks>
    /// It must be last: the runtime feeds each provider the previous one's output, so only the final
    /// position sees everything the others contributed. That is why this is called from the tail of
    /// <see cref="BuildMergedAIContextProviders"/> rather than by whoever consumes the rail — lastness is
    /// then a property of the builder, not of a call sequence somewhere else. Placing it after
    /// <see cref="Services.Agent.GoverningToolContextProvider"/> — which is itself documented as going last
    /// — is safe, because this provider neither adds nor removes tools and so cannot escape that governance
    /// wrapper or defeat it. Nothing is appended when no budget tracker is wired in, leaving a host that
    /// does not track context with exactly the rail it had before.
    /// </remarks>
    private void AppendPerTurnBudgetProvider(
        IList<AIContextProvider> providers,
        string agentName,
        string instruction,
        int toolCount)
    {
        if (_budgetTracker is null || providers.Count == 0)
            return;

        providers.Add(new Services.Agent.PerTurnBudgetContextProvider(
            agentName,
            _budgetTracker,
            instruction,
            toolCount,
            _loggerFactory.CreateLogger<Services.Agent.PerTurnBudgetContextProvider>()));

        _logger.LogDebug(
            "Wired PerTurnBudgetContextProvider for {AgentName} behind {ProviderCount} context provider(s)",
            agentName, providers.Count - 1);
    }
}
