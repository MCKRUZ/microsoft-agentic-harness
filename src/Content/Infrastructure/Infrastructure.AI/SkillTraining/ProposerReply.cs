using System.Text.RegularExpressions;

namespace Infrastructure.AI.SkillTraining;

/// <summary>
/// What the proposer session replied: the complete new skill document, when it wants to change the
/// skill, and its notes (#843).
/// </summary>
/// <remarks>
/// <para>
/// The document sits between a <c>===BEGIN SKILL===</c> line and a <c>===END SKILL===</c> line, each on a
/// line of its own, and the notes between <c>===BEGIN NOTES===</c> and <c>===END NOTES===</c>. They are not
/// XML-style tags because a skill that documents reply formats contains tags of its own, and a lazy tag
/// match would cut the document at the first of them and hand the loop a fragment that still reproduces
/// "exactly".
/// </para>
/// <para>
/// A reply whose document is ambiguous (a delimiter line inside it, or another one after it) is flagged
/// by <see cref="IsAmbiguous"/> and refused by the proposer rather than guessed at. A reply with no
/// blocks at all, such as a bare <c>KEEP_BASE</c>, is carried whole as the notes.
/// </para>
/// </remarks>
/// <param name="Skill">The complete new document, or <see langword="null"/> when the reply carried none.</param>
/// <param name="Notes">The notes block; when the reply had no blocks at all, the reply text itself.</param>
/// <param name="IsAmbiguous">Whether the document block cannot be told apart from the delimiters around it.</param>
internal sealed partial record ProposerReply(string? Skill, string Notes, bool IsAmbiguous = false)
{
    /// <summary>The line that opens the document.</summary>
    public const string BeginSkill = "===BEGIN SKILL===";

    /// <summary>The line that closes the document.</summary>
    public const string EndSkill = "===END SKILL===";

    /// <summary>The line that opens the notes.</summary>
    public const string BeginNotes = "===BEGIN NOTES===";

    /// <summary>The line that closes the notes.</summary>
    public const string EndNotes = "===END NOTES===";

    private const RegexOptions Lines = RegexOptions.Singleline | RegexOptions.Multiline | RegexOptions.CultureInvariant;

    [GeneratedRegex(@"^===BEGIN SKILL===[ \t]*\r?\n(.*?)\r?\n?^===END SKILL===[ \t]*\r?$", Lines)]
    private static partial Regex SkillBlock();

    [GeneratedRegex(@"^===BEGIN NOTES===[ \t]*\r?\n(.*?)\r?\n?^===END NOTES===[ \t]*\r?$", Lines)]
    private static partial Regex NotesBlock();

    [GeneratedRegex(@"^===(?:BEGIN|END) SKILL===[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex SkillDelimiterLine();

    /// <summary>Whether the notes say the skill should be left as it is.</summary>
    public bool SaysKeepBase => Notes.Contains("KEEP_BASE", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the last skill and notes blocks of a reply (the last, because a model that quotes the format
    /// while thinking aloud puts its real answer at the end). Line endings in the document are normalised
    /// to <c>\n</c>.
    /// </summary>
    /// <param name="reply">The session's final response.</param>
    public static ProposerReply Parse(string reply)
    {
        ArgumentNullException.ThrowIfNull(reply);

        var notes = NotesBlock().Matches(reply).LastOrDefault()?.Groups[1].Value.Trim();
        var block = SkillBlock().Matches(reply).LastOrDefault();
        if (block is null)
            return new ProposerReply(null, notes ?? reply.Trim());

        var skill = block.Groups[1].Value.Replace("\r\n", "\n", StringComparison.Ordinal);
        var tail = reply[(block.Index + block.Length)..];
        var ambiguous = SkillDelimiterLine().IsMatch(block.Groups[1].Value) || SkillDelimiterLine().IsMatch(tail);

        return new ProposerReply(skill, notes ?? string.Empty, ambiguous);
    }
}
