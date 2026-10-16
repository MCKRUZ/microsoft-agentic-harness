using System.Text.RegularExpressions;

namespace Infrastructure.AI.SkillTraining;

/// <summary>
/// What the proposer session replied: the complete new skill document, when it wants to change the
/// skill, and its notes (#843).
/// </summary>
/// <param name="Skill">The complete new document, or <see langword="null"/> when the reply carried none.</param>
/// <param name="Notes">The notes block, or empty.</param>
internal sealed partial record ProposerReply(string? Skill, string Notes)
{
    [GeneratedRegex(@"<skill>(.*?)</skill>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SkillBlock();

    [GeneratedRegex(@"<notes>(.*?)</notes>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotesBlock();

    /// <summary>Whether the notes say the skill should be left as it is.</summary>
    public bool SaysKeepBase => Notes.Contains("KEEP_BASE", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the last <c>&lt;skill&gt;</c> and <c>&lt;notes&gt;</c> blocks of a reply (the last, because a model
    /// that quotes the format while thinking aloud puts its real answer at the end). Line endings are
    /// normalised, and the one newline the tags put after <c>&lt;skill&gt;</c> and before <c>&lt;/skill&gt;</c> is removed.
    /// </summary>
    /// <param name="reply">The session's final response.</param>
    public static ProposerReply Parse(string reply)
    {
        ArgumentNullException.ThrowIfNull(reply);

        var skill = SkillBlock().Matches(reply).LastOrDefault()?.Groups[1].Value;
        var notes = NotesBlock().Matches(reply).LastOrDefault()?.Groups[1].Value.Trim() ?? string.Empty;

        if (skill is null)
            return new ProposerReply(null, notes);

        skill = skill.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (skill.StartsWith('\n'))
            skill = skill[1..];
        if (skill.EndsWith('\n'))
            skill = skill[..^1];

        return new ProposerReply(skill, notes);
    }
}
