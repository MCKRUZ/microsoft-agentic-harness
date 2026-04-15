namespace Domain.Common.MetaHarness;

/// <summary>
/// Immutable output from <see cref="Application.AI.Common.Interfaces.MetaHarness.IHarnessProposer"/>
/// representing a set of proposed harness changes derived from trace analysis.
/// </summary>
public sealed record HarnessProposal
{
    /// <summary>
    /// Skill file path to full replacement content.
    /// Empty dictionary when no skill file changes are proposed.
    /// </summary>
    public required IReadOnlyDictionary<string, string> ProposedSkillChanges { get; init; }

    /// <summary>
    /// Config key to new string value.
    /// Empty dictionary when no config changes are proposed.
    /// </summary>
    public required IReadOnlyDictionary<string, string> ProposedConfigChanges { get; init; }

    /// <summary>Replacement system prompt; <c>null</c> when no system prompt change is proposed.</summary>
    public string? ProposedSystemPromptChange { get; init; }

    /// <summary>Agent's explanation of why these changes were proposed.</summary>
    public required string Reasoning { get; init; }

    /// <summary>
    /// Agent's observations about what worked, what failed, and patterns noticed this iteration.
    /// Appended to <c>learnings.md</c> and passed back as <see cref="HarnessProposerContext.PriorLearnings"/>
    /// in the next iteration. <c>null</c> when the agent did not include a learnings entry.
    /// </summary>
    public string? Learnings { get; init; }
}
