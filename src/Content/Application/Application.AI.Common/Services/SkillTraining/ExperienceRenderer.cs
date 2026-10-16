using System.Globalization;
using System.Text;
using Domain.AI.SkillTraining;

namespace Application.AI.Common.Services.SkillTraining;

/// <summary>
/// Renders experience entries as the text the proposer reads (#845).
/// </summary>
/// <remarks>
/// Shows the cue, the lesson, the basis and the confidence, in the order the entries were given. It does not
/// show the skill an entry was drawn against: the proposer reads the current skill separately, and a stale
/// copy beside it would only be confusing. Only the most recent <see cref="MaxEntries"/> are shown so the
/// memory cannot grow without bound across a long run.
/// </remarks>
public static class ExperienceRenderer
{
    /// <summary>The most entries rendered; earlier ones are dropped from the text, not from the run.</summary>
    public const int MaxEntries = 10;

    /// <summary>Renders <paramref name="entries"/>; empty text for none.</summary>
    /// <param name="entries">The entries, oldest first.</param>
    public static string Render(IReadOnlyList<ExperienceEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
            return string.Empty;

        var text = new StringBuilder("# Lessons from earlier rounds\n");
        foreach (var entry in entries.Skip(Math.Max(0, entries.Count - MaxEntries)))
        {
            text.Append("\n## ").Append(entry.Domain).Append(", epoch ").Append(entry.Epoch).Append('\n')
                .Append("When: ").Append(entry.Cue).Append('\n')
                .Append(entry.Lesson).Append('\n');
            if (!string.IsNullOrWhiteSpace(entry.Evidence))
                text.Append("Basis: ").Append(entry.Evidence).Append('\n');
            text.Append("Confidence: ").Append(entry.Confidence.ToString("0.##", CultureInfo.InvariantCulture)).Append('\n');
        }

        return text.ToString().TrimEnd();
    }
}
