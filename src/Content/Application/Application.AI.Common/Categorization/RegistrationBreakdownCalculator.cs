using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces.Context;
using Domain.AI.Context;

namespace Application.AI.Common.Categorization;

/// <summary>
/// Sums a <see cref="RegistrationSnapshot"/> into the per-category token totals the Foresight context
/// bar renders — the measured replacement for the residual subtraction that used to stand in for
/// them (#507).
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the single definition of what each lane costs.</strong> The same arithmetic feeds
/// the inspector drawer's per-item rows (via <c>AppendRegistrationItems</c>), and the two must agree:
/// a bar and a drawer describing the same turn with different numbers is worse than either being
/// slightly wrong, because there is then no way to tell which one to believe. The drawer itemizes the
/// per-turn <em>delta</em> while this sums the <em>cumulative</em> state, so they answer different
/// questions — but from identical per-item measurements.
/// </para>
/// <para>
/// Every figure comes from text that was actually loaded into the agent, so this works the same
/// whether or not the optional prompt-composition feature is switched on. That matters: composition is
/// off by default and enabled in no shipped configuration, so a fix that depended on it would have
/// left the default deployment exactly as broken as before.
/// </para>
/// </remarks>
public static class RegistrationBreakdownCalculator
{
    /// <summary>
    /// Computes the cumulative per-category totals for everything registered into the agent's context.
    /// </summary>
    /// <param name="snapshot">The full current registration state — not a delta.</param>
    /// <returns>
    /// A breakdown whose registration lanes are populated and whose
    /// <see cref="CategoryBreakdown.Messages"/> is zero: the transcript is measured by
    /// <see cref="DefaultContextSnapshotComputer"/> from the message history, and populating it here
    /// as well would double-count it.
    /// </returns>
    public static CategoryBreakdown From(RegistrationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var skillTokens = snapshot.Skills.Sum(TokensFor);

        return CategoryBreakdown.Empty
            .Add(ContextCategory.System, SystemPromptTokens(snapshot, skillTokens))
            .Add(ContextCategory.Skills, skillTokens)
            .Add(ContextCategory.Tools, snapshot.NativeTools.Sum(TokensFor))
            .Add(ContextCategory.Mcp, snapshot.McpTools.Sum(TokensFor))
            .Add(ContextCategory.Agents, snapshot.SubAgents.Sum(TokensFor));
    }

    /// <summary>
    /// The system-prompt lane: the agent's whole instruction minus the skill instructions embedded
    /// within it.
    /// </summary>
    /// <remarks>
    /// This subtraction is deliberate and is not the defect #507 was about, though it shares a shape.
    /// The agent's instruction text <em>contains</em> its skills' instructions, so charging both in
    /// full would count that text twice and inflate the bar past what the model receives. Both terms
    /// are the same estimator over real loaded text, so the difference is meaningful — unlike the old
    /// <c>System = billed − estimated-messages</c>, which subtracted an estimate of one thing from a
    /// measurement of another and floored to zero whenever the estimate ran long.
    /// <para>
    /// The clamp is still a real (if narrow) blind spot: it fires only when a registered skill's text
    /// is <em>not</em> embedded in the instruction, and reports the system prompt as smaller than it is
    /// rather than reporting the inconsistency. <see cref="ContextSnapshot.UnaccountedTokens"/> is
    /// where that surfaces — a turn losing tokens here widens the gap rather than hiding it.
    /// </para>
    /// </remarks>
    private static int SystemPromptTokens(RegistrationSnapshot snapshot, int skillTokens) =>
        string.IsNullOrEmpty(snapshot.SystemPromptText)
            ? 0
            : Math.Max(0, TokenEstimationHelper.EstimateTokens(snapshot.SystemPromptText) - skillTokens);

    private static int TokensFor(SkillRegistration skill) =>
        TokenEstimationHelper.EstimateTokens(skill.InstructionsText);

    private static int TokensFor(AgentRegistration agent) =>
        TokenEstimationHelper.EstimateTokens(agent.Description);

    /// <summary>
    /// A tool's footprint: its serialized schema when it has one, else its name and description.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>ExecuteAgentTurnCommandHandler.EstimateToolTokens</c> exactly. A tool without a
    /// serialized schema (a non-<c>AIFunction</c> tool) still occupies context, so it falls back to
    /// the text that does reach the model rather than reporting zero.
    /// </remarks>
    private static int TokensFor(ToolRegistration tool) =>
        !string.IsNullOrEmpty(tool.SchemaText)
            ? TokenEstimationHelper.EstimateTokens(tool.SchemaText)
            : TokenEstimationHelper.EstimateTokens(
                (tool.Name ?? string.Empty) + " " + (tool.Description ?? string.Empty));
}
