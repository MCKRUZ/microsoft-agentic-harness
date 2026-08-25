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
/// questions — but through these same methods, not a copy of them. An earlier draft asserted exactly
/// this while the handler kept its own hand-copied estimators; the claim is now structural.
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
    /// The system-prompt lane for one snapshot: the agent's whole instruction minus the skill
    /// instructions embedded within it.
    /// </summary>
    /// <param name="snapshot">The registration state to size.</param>
    public static int SystemPromptTokens(RegistrationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return SystemPromptTokens(snapshot, snapshot.Skills.Sum(TokensFor));
    }

    /// <summary>
    /// The system-prompt lane: the agent's whole instruction minus the skill instructions embedded
    /// within it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The subtrahend is the <em>inlined</em> skill total, not every registered skill, and that
    /// distinction is the whole point. The instruction contains the bodies it inlined, so charging
    /// those to both lanes would count the same text twice; it does <em>not</em> contain the bodies the
    /// framework serves on demand, so subtracting those removes text that was never there. An earlier
    /// draft subtracted all of them and described the resulting clamp as a narrow edge case — the
    /// reverse of the truth, since on-demand disclosure is the default path. That reproduced #507's own
    /// symptom inside its fix: a real system prompt reported as zero.
    /// </para>
    /// <para>
    /// With both terms drawn from the same set, each inlined body is a literal substring of the
    /// instruction, so on the default merge path <c>System + Skills</c> reconciles to it and the clamp
    /// does not fire. Two cases keep it necessary rather than defensive:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <strong>Prompt composition.</strong> When that opt-in feature is on, the merged text is
    ///     handed to a composer that reassembles it section-by-section within a token budget and drops
    ///     low-priority sections. The result is a rewrite, not a superset — it can be shorter than the
    ///     bodies it nominally contains.
    ///   </description></item>
    ///   <item><description>
    ///     <strong>A producer this code does not control.</strong> <see cref="RegistrationSnapshot"/>
    ///     is handed in; a caller that mislabels a held-back body as inlined should get a flat lane,
    ///     not a negative one.
    ///   </description></item>
    /// </list>
    /// <para>
    /// In both, the clamp trades a wrong-and-visible number for a wrong-and-quiet one, which is a
    /// limitation rather than a fix — reconciling it needs the per-turn prompt measurement deferred to
    /// #517. Saying so is the point: an earlier draft called the clamp unreachable, and that confidence
    /// is exactly what let the original defect hide.
    /// </para>
    /// </remarks>
    private static int SystemPromptTokens(RegistrationSnapshot snapshot, int skillTokens) =>
        string.IsNullOrEmpty(snapshot.SystemPromptText)
            ? 0
            : Math.Max(0, TokenEstimationHelper.EstimateTokens(snapshot.SystemPromptText) - skillTokens);

    /// <summary>
    /// What one skill costs in the prompt: its instruction body when that body was inlined, otherwise
    /// nothing.
    /// </summary>
    /// <remarks>
    /// A skill served on demand has its body held out of the system prompt deliberately, so it is not
    /// occupying the context window and must not be charged as though it were. Counting it would
    /// inflate the skills lane with text the model never received and — because the system lane is the
    /// instruction minus this figure — deflate the system lane by the same amount, which is how a real
    /// system prompt came to read as zero in the first place (#507).
    /// <para>
    /// What happens to that body once the skill IS loaded is not settled here, and is worth knowing
    /// before trusting the lane: it arrives as a tool result mid-turn, and whether it later shows up in
    /// the transcript estimate depends on the durable tool-call replay path putting tool messages back
    /// into the dispatched history on a subsequent turn. Not traced to a conclusion, so this comment
    /// does not claim it.
    /// </para>
    /// </remarks>
    public static int TokensFor(SkillRegistration skill)
    {
        ArgumentNullException.ThrowIfNull(skill);

        return skill.InlinedInPrompt
            ? TokenEstimationHelper.EstimateTokens(skill.InstructionsText)
            : 0;
    }

    /// <summary>One delegatable peer agent's description.</summary>
    public static int TokensFor(AgentRegistration agent) =>
        TokenEstimationHelper.EstimateTokens(agent.Description);

    /// <summary>
    /// A tool's footprint: its serialized schema when it has one, else its name and description.
    /// </summary>
    /// <remarks>
    /// A tool without a serialized schema (a non-<c>AIFunction</c> tool) still occupies context, so it
    /// falls back to the text that does reach the model rather than reporting zero.
    /// </remarks>
    public static int TokensFor(ToolRegistration tool) =>
        !string.IsNullOrEmpty(tool.SchemaText)
            ? TokenEstimationHelper.EstimateTokens(tool.SchemaText)
            : TokenEstimationHelper.EstimateTokens(
                (tool.Name ?? string.Empty) + " " + (tool.Description ?? string.Empty));
}
