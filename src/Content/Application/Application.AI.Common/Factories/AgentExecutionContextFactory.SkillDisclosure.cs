using Application.AI.Common.Helpers;
using Domain.AI.Skills;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Factories;

/// <summary>
/// Progressive-disclosure half of <see cref="AgentExecutionContextFactory"/>: makes the skills the
/// framework will serve on demand chargeable to the agent's budget, and reports the ones that had to
/// stay eagerly in the static prompt.
/// </summary>
/// <remarks>
/// Disclosure only pays for itself if its cost is visible. A skill served on demand is invisible to
/// the factory's up-front budget records — the tokens arrive later, when the model pulls the body —
/// and a skill that quietly falls back to eager injection is invisible too, because the agent keeps
/// working and only the prompt grows. The two members here close both gaps: one charges the pulls,
/// the other names the fallbacks.
/// </remarks>
public partial class AgentExecutionContextFactory
{
    /// <summary>
    /// Wraps each disclosable skill so the tokens its Tier 2 body and Tier 3 files put into the context are
    /// charged to <paramref name="agentName"/>'s budget when the model pulls them.
    /// </summary>
    /// <param name="disclosableSkills">The skills about to be handed to the framework provider.</param>
    /// <param name="agentName">The agent whose budget the pulls are charged to.</param>
    /// <returns>
    /// The same skills, each wrapped — or the input unchanged when no budget tracker is wired in, so a host
    /// that does not track context passes the framework's own skill objects through untouched.
    /// </returns>
    private IReadOnlyList<DisclosableSkill> ChargeSkillLoadsToBudget(
        IReadOnlyList<DisclosableSkill> disclosableSkills,
        string agentName)
    {
        if (_budgetTracker is null || disclosableSkills.Count == 0)
            return disclosableSkills;

        return [.. disclosableSkills.Select(s => s with
        {
            Skill = new Services.Skills.BudgetChargingSkill(s.Skill, agentName, _budgetTracker)
        })];
    }

    /// <summary>
    /// Records which skills kept their full body in the static prompt because the framework provider will
    /// not serve them on demand.
    /// </summary>
    /// <remarks>
    /// Falling back to eager injection is the safe outcome, but it is also invisible — the agent works,
    /// the prompt is just larger than it should be. Without this line the only symptom of a skill that can
    /// no longer be registered (a name edited out of kebab-case, a description deleted) is a gradual return
    /// of the token cost progressive disclosure exists to remove. <see cref="DisclosableSkillFactory"/>
    /// names a reason for the skills it rejects; it stays silent about ones it never considered, so this
    /// total is the only signal covering those.
    /// </remarks>
    private void LogSkillsExcludedFromDisclosure(
        IReadOnlyList<SkillDefinition> skills,
        IReadOnlySet<string> disclosedOnDemand)
    {
        if (!_logger.IsEnabled(LogLevel.Debug))
            return;

        var eager = skills
            .Where(s => !string.IsNullOrEmpty(s.Instructions) && !disclosedOnDemand.Contains(s.Id))
            .Select(s => s.Id)
            .ToList();

        if (eager.Count == 0)
            return;

        _logger.LogDebug(
            "Skill instructions kept in the static prompt for {Count} skill(s) not registered with the " +
            "framework skills provider: {SkillIds}",
            eager.Count, string.Join(", ", eager));
    }
}
