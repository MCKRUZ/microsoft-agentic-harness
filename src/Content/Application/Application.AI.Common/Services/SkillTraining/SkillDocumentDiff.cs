using Domain.AI.SkillTraining;

namespace Application.AI.Common.Services.SkillTraining;

/// <summary>
/// Turns "here is the new skill document" into the bounded edits the training loop works in (#843).
/// </summary>
/// <remarks>
/// <para>
/// The loop's unit of change is an <see cref="Edit"/>: insert after a located text, replace one, delete
/// one, or append. A proposer that works on files (the agentic proposer reads the skill, thinks, and
/// returns the whole new document) has to be translated into that vocabulary so the fence, the
/// aggregator, the selector and the gate see the same kind of patch as ever.
/// </para>
/// <para>
/// <b>Exact by construction.</b> <see cref="PatchApplier"/> finds a target with the first ordinal
/// occurrence and applies edits one after another to the evolving text. This class simulates exactly that
/// while it builds the edits, widening each target with surrounding lines until it occurs at the intended
/// place first, so <c>PatchApplier.Apply(old, edits)</c> reproduces <c>new</c> byte for byte. The caller
/// still verifies that with the real applier and rejects the proposal when it does not hold.
/// </para>
/// <para>
/// One edit per changed hunk (a run of changed lines), from a line-level longest-common-subsequence
/// diff, so the edit budget the loop spends per step means what it did before: a proposer that changes
/// three separate places proposes three edits, not one edit spanning the whole document. The lines shared
/// at the start and end are set aside before the diff, and a change too large for the diff table falls
/// back to a single hunk between the common prefix and suffix.
/// </para>
/// </remarks>
public static class SkillDocumentDiff
{
    /// <summary>
    /// Above this many line pairs (in what is left after the shared start and end are removed) the
    /// quadratic diff is skipped for a single hunk. The table is four bytes a pair, so this is 16 MB.
    /// </summary>
    private const long MaxLinePairs = 4_000_000;

    /// <summary>Computes the edits that turn <paramref name="oldText"/> into <paramref name="newText"/>.</summary>
    /// <param name="oldText">The current skill document.</param>
    /// <param name="newText">The skill document the proposer returned.</param>
    /// <returns>The edits in application order; empty when the texts are equal.</returns>
    public static IReadOnlyList<Edit> Between(string oldText, string newText)
    {
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);

        if (string.Equals(oldText, newText, StringComparison.Ordinal))
            return [];

        if (oldText.Length == 0)
            return [new Edit { Op = EditOp.Append, Content = newText }];

        var oldLines = SplitKeepingNewlines(oldText);
        var newLines = SplitKeepingNewlines(newText);
        var hunks = Hunks(oldLines, newLines);

        var working = oldText;
        var edits = new List<Edit>(hunks.Count);
        var shift = 0; // how much earlier hunks have grown or shrunk the working text
        foreach (var hunk in hunks)
        {
            var start = OffsetOf(oldLines, hunk.OldStart) + shift;
            var removed = string.Concat(oldLines.Skip(hunk.OldStart).Take(hunk.OldCount));
            var added = string.Concat(newLines.Skip(hunk.NewStart).Take(hunk.NewCount));

            var edit = BuildEdit(working, start, removed, added);
            edits.Add(edit);
            working = Apply(working, edit);
            shift += added.Length - removed.Length;
        }

        return edits;
    }

    private static Edit BuildEdit(string working, int start, string removed, string added)
    {
        var end = start + removed.Length;

        if (removed.Length == 0)
        {
            // Pure insertion. At the very top there is no text before it to anchor on, so anchor on the
            // first line and put the addition in front of it. A prefix is always the first occurrence of
            // itself, so that anchor needs no widening.
            if (start == 0)
            {
                var firstLine = working[..NextLineEnd(working, 0)];
                return new Edit { Op = EditOp.Replace, Target = firstLine, Content = added + firstLine };
            }

            var anchorStart = WidenLeft(working, start, start);
            return new Edit { Op = EditOp.InsertAfter, Target = working[anchorStart..start], Content = added };
        }

        var targetStart = WidenLeft(working, start, end);
        var leftContext = working[targetStart..start];

        // Removing text with nothing around it that needs naming: a plain Delete.
        if (added.Length == 0 && leftContext.Length == 0)
            return new Edit { Op = EditOp.Delete, Target = removed };

        // The applier rejects an empty replacement, so a removal that had to be anchored on a preceding
        // line is expressed as replacing "that line + the removed text" with "that line".
        return new Edit
        {
            Op = EditOp.Replace,
            Target = working[targetStart..end],
            Content = leftContext + added
        };
    }

    /// <summary>
    /// Moves the start of <c>[start, end)</c> back by whole lines until that text occurs first at its own
    /// position, which is what <see cref="PatchApplier"/> will find. Terminates: at the top of the document
    /// the text is a prefix and a prefix is always the first occurrence of itself.
    /// </summary>
    private static int WidenLeft(string text, int start, int end)
    {
        var s = start;
        while (s > 0)
        {
            var candidate = text[s..end];
            if (candidate.Length > 0 && text.IndexOf(candidate, StringComparison.Ordinal) == s)
                return s;
            s = PreviousLineStart(text, s);
        }

        return 0;
    }

    private static int PreviousLineStart(string text, int index)
    {
        // index is at a line start (or 0): step back over the previous line, newline included.
        var i = index - 1;
        while (i > 0 && text[i - 1] != '\n')
            i--;
        return Math.Max(i, 0);
    }

    private static int NextLineEnd(string text, int index)
    {
        var i = text.IndexOf('\n', index);
        return i < 0 ? text.Length : i + 1;
    }

    private static string Apply(string content, Edit edit)
    {
        var idx = edit.Op == EditOp.Append ? -1 : content.IndexOf(edit.Target, StringComparison.Ordinal);
        return edit.Op switch
        {
            EditOp.Append => content.Length == 0 ? edit.Content : content + "\n\n" + edit.Content,
            EditOp.InsertAfter => content[..(idx + edit.Target.Length)] + edit.Content + content[(idx + edit.Target.Length)..],
            EditOp.Replace => content[..idx] + edit.Content + content[(idx + edit.Target.Length)..],
            _ => content[..idx] + content[(idx + edit.Target.Length)..]
        };
    }

    private static int OffsetOf(IReadOnlyList<string> lines, int lineIndex)
    {
        var offset = 0;
        for (var i = 0; i < lineIndex; i++)
            offset += lines[i].Length;
        return offset;
    }

    private static List<string> SplitKeepingNewlines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var nl = text.IndexOf('\n', start);
            var end = nl < 0 ? text.Length : nl + 1;
            lines.Add(text[start..end]);
            start = end;
        }

        return lines;
    }

    private readonly record struct Hunk(int OldStart, int OldCount, int NewStart, int NewCount);

    /// <summary>
    /// The changed hunks between two line lists. The lines the two share at the start and at the end are
    /// peeled off first, so the quadratic table is built only over what actually moved: a skill edited in
    /// one place costs almost nothing however long it is. If what is left is still too large for the
    /// table, it becomes one hunk.
    /// </summary>
    private static List<Hunk> Hunks(List<string> oldLines, List<string> newLines)
    {
        var prefix = 0;
        while (prefix < oldLines.Count && prefix < newLines.Count && oldLines[prefix] == newLines[prefix])
            prefix++;

        var suffix = 0;
        while (suffix < oldLines.Count - prefix && suffix < newLines.Count - prefix
               && oldLines[^(suffix + 1)] == newLines[^(suffix + 1)])
            suffix++;

        var oldMiddle = oldLines.GetRange(prefix, oldLines.Count - prefix - suffix);
        var newMiddle = newLines.GetRange(prefix, newLines.Count - prefix - suffix);

        if ((long)oldMiddle.Count * newMiddle.Count > MaxLinePairs)
            return [new Hunk(prefix, oldMiddle.Count, prefix, newMiddle.Count)];

        return [.. LineHunks(oldMiddle, newMiddle).Select(h => h with { OldStart = h.OldStart + prefix, NewStart = h.NewStart + prefix })];
    }

    /// <summary>Runs of changed lines between two line lists, from a longest-common-subsequence table.</summary>
    private static List<Hunk> LineHunks(List<string> oldLines, List<string> newLines)
    {
        var n = oldLines.Count;
        var m = newLines.Count;
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = oldLines[i] == newLines[j]
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var hunks = new List<Hunk>();
        int a = 0, b = 0;
        while (a < n || b < m)
        {
            if (a < n && b < m && oldLines[a] == newLines[b])
            {
                a++;
                b++;
                continue;
            }

            var hunkOld = a;
            var hunkNew = b;
            while ((a < n || b < m) && !(a < n && b < m && oldLines[a] == newLines[b]))
            {
                if (b >= m || (a < n && lcs[a + 1, b] >= lcs[a, b + 1]))
                    a++;
                else
                    b++;
            }

            hunks.Add(new Hunk(hunkOld, a - hunkOld, hunkNew, b - hunkNew));
        }

        return hunks;
    }
}
