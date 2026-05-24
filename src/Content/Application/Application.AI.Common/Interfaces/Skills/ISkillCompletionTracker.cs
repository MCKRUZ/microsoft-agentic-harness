namespace Application.AI.Common.Interfaces.Skills;

/// <summary>
/// Tracks skill completion state per conversation. Used by the prerequisite
/// system to determine when a skill's dependent tools should be unlocked.
/// </summary>
/// <remarks>
/// Skills declare a <c>completion_tool</c> in their SKILL.md frontmatter. When that tool
/// is invoked without error during a conversation turn, the skill is marked as completed.
/// Downstream skills with prerequisites referencing this skill then have their tools unlocked.
/// </remarks>
public interface ISkillCompletionTracker
{
    /// <summary>
    /// Marks a skill as completed for the given conversation.
    /// </summary>
    /// <param name="conversationId">The conversation scope.</param>
    /// <param name="skillId">The skill that completed.</param>
    void MarkCompleted(string conversationId, string skillId);

    /// <summary>
    /// Checks whether a skill has been completed in the given conversation.
    /// </summary>
    /// <param name="conversationId">The conversation scope.</param>
    /// <param name="skillId">The skill to check.</param>
    /// <returns>True if the skill has been marked as completed.</returns>
    bool IsCompleted(string conversationId, string skillId);

    /// <summary>
    /// Returns all skill IDs that have been completed in the given conversation.
    /// </summary>
    /// <param name="conversationId">The conversation scope.</param>
    /// <returns>Set of completed skill IDs. Empty if none or conversation unknown.</returns>
    IReadOnlySet<string> GetCompletedSkills(string conversationId);

    /// <summary>
    /// Removes all completion state for a conversation. Called when the conversation ends
    /// or the agent cache evicts the entry.
    /// </summary>
    /// <param name="conversationId">The conversation to clear.</param>
    void ClearConversation(string conversationId);
}
