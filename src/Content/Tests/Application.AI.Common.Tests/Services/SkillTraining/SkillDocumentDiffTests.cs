using System.Text.RegularExpressions;
using Application.AI.Common.Services.SkillTraining;
using Domain.AI.SkillTraining;
using FluentAssertions;

namespace Application.AI.Common.Tests.Services.SkillTraining;

/// <summary>
/// Pins <see cref="SkillDocumentDiff"/> (#843): whatever new document a proposer returns, the edits it
/// is turned into reproduce that document exactly when run through the real <see cref="PatchApplier"/>.
/// The documents here repeat lines on purpose, because a repeated line is what makes the applier's
/// "first occurrence" rule land an edit in the wrong place.
/// </summary>
public sealed class SkillDocumentDiffTests
{
    private static readonly PatchApplier Applier = new();

    private static string Applied(string oldText, IReadOnlyList<Edit> edits)
    {
        var report = Applier.Apply(oldText, new Patch { Edits = edits });
        report.FailedEdits.Should().BeEmpty("every edit must locate its target");
        return report.NewSkillContent;
    }

    [Fact]
    public void Between_EqualTexts_NeedsNoEdits()
    {
        SkillDocumentDiff.Between("a\nb\n", "a\nb\n").Should().BeEmpty();
    }

    [Fact]
    public void Between_EmptyDocument_AppendsTheWholeNewText()
    {
        var edits = SkillDocumentDiff.Between(string.Empty, "# new\n- rule\n");

        edits.Should().ContainSingle().Which.Op.Should().Be(EditOp.Append);
        Applied(string.Empty, edits).Should().Be("# new\n- rule\n");
    }

    [Fact]
    public void Between_InsertionAtTheTop_IsExpressedWithoutATargetBeforeIt()
    {
        var old = "first\nsecond\n";
        var edits = SkillDocumentDiff.Between(old, "zero\nfirst\nsecond\n");

        Applied(old, edits).Should().Be("zero\nfirst\nsecond\n");
    }

    [Fact]
    public void Between_DeletingOneOfSeveralIdenticalLines_RemovesThatOneNotTheFirst()
    {
        var old = "rule\nrule\nrule\ntail\n";
        var edits = SkillDocumentDiff.Between(old, "rule\nrule\ntail\n");

        Applied(old, edits).Should().Be("rule\nrule\ntail\n");
    }

    [Fact]
    public void Between_ChangeInTheLastOfTwoIdenticalBlocks_LeavesTheFirstBlockAlone()
    {
        var old = "## A\n- x\n## B\n- x\n";
        var edits = SkillDocumentDiff.Between(old, "## A\n- x\n## B\n- y\n");

        Applied(old, edits).Should().Be("## A\n- x\n## B\n- y\n");
    }

    [Fact]
    public void Between_SeparateChanges_BecomeSeparateEdits()
    {
        var old = "a\nb\nc\nd\ne\nf\ng\n";
        var edits = SkillDocumentDiff.Between(old, "a\nB\nc\nd\ne\nF\ng\n");

        edits.Should().HaveCount(2, "a proposer that changes two places proposes two edits, not one spanning both");
        Applied(old, edits).Should().Be("a\nB\nc\nd\ne\nF\ng\n");
    }

    [Fact]
    public void Between_NoTrailingNewline_StillReproducesTheText()
    {
        var old = "a\nb";
        Applied(old, SkillDocumentDiff.Between(old, "a\nb\nc")).Should().Be("a\nb\nc");
        Applied("a\nb\nc", SkillDocumentDiff.Between("a\nb\nc", "a\nb")).Should().Be("a\nb");
    }

    [Fact]
    public void Between_ChangesTooFarApartForTheLineDiff_FallBackToOneHunkAndStillReproduce()
    {
        // Changed at both ends, so nothing at the start or end is shared and the whole document is left to diff.
        var lines = Enumerable.Range(0, 3000).Select(i => $"line {i % 50}\n").ToArray();
        var old = string.Concat(lines);
        var changedLines = (string[])lines.Clone();
        changedLines[0] = "FIRST\n";
        changedLines[^1] = "LAST\n";
        var changed = string.Concat(changedLines);

        var edits = SkillDocumentDiff.Between(old, changed);

        edits.Should().ContainSingle("the change is too large to diff line by line");
        Applied(old, edits).Should().Be(changed);
    }

    [Fact]
    public void Between_OneChangeInALongDocument_IsDiffedWithoutBuildingATableForTheWholeDocument()
    {
        // 5000 x 5000 lines is 25 million pairs: ~100 MB if the table were built over the whole document.
        // The lines shared at the start and the end are set aside first, so the table is over one line.
        var old = string.Concat(Enumerable.Range(0, 5000).Select(i => $"line {i}\n"));
        var changed = old.Replace("line 2500\n", "line 2500 reworded\n", StringComparison.Ordinal);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var edits = SkillDocumentDiff.Between(old, changed);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        edits.Should().ContainSingle();
        Applied(old, edits).Should().Be(changed);
        allocated.Should().BeLessThan(20_000_000, "the diff table is built only over what changed");
    }

    [Fact]
    public void Between_TwoDistantChangesInALongDocument_StayTwoEditsAndLandOnTheRightOccurrence()
    {
        var shared = string.Concat(Enumerable.Range(0, 400).Select(i => $"keep {i}\n"));
        var old = shared + "alpha\n" + shared + "omega\n" + shared;
        var changed = shared + "ALPHA\n" + shared + "OMEGA\n" + shared;

        var edits = SkillDocumentDiff.Between(old, changed);

        edits.Should().HaveCount(2);
        Applied(old, edits).Should().Be(changed);
    }

    [Fact]
    public void Between_RandomDocumentsWithManyRepeatedLines_AlwaysReproduceTheNewText()
    {
        // Few distinct lines, many edits: the shape most likely to trip the first-occurrence rule.
        var alphabet = new[] { "- keep it short\n", "- cite the source\n", "## Rules\n", "\n", "- check the total\n", "tail" };
        var random = new Random(8431);

        for (var round = 0; round < 600; round++)
        {
            var old = RandomDocument(random, alphabet);
            var changed = Mutate(random, old, alphabet);

            var edits = SkillDocumentDiff.Between(old, changed);

            Applied(old, edits).Should().Be(changed, $"round {round}: old={Show(old)} new={Show(changed)}");
        }
    }

    private static string RandomDocument(Random random, string[] alphabet)
    {
        var lines = Enumerable.Range(0, random.Next(0, 14)).Select(_ => alphabet[random.Next(alphabet.Length)]);
        return string.Concat(lines);
    }

    private static string Mutate(Random random, string text, string[] alphabet)
    {
        var parts = new List<string>(Regex.Matches(text, "[^\n]*\n|[^\n]+").Select(m => m.Value));
        for (var edits = random.Next(1, 4); edits > 0; edits--)
        {
            var at = random.Next(0, parts.Count + 1);
            switch (random.Next(3))
            {
                case 0:
                    parts.Insert(at, alphabet[random.Next(alphabet.Length)]);
                    break;
                case 1 when parts.Count > 0:
                    parts.RemoveAt(Math.Min(at, parts.Count - 1));
                    break;
                case 2 when parts.Count > 0:
                    parts[Math.Min(at, parts.Count - 1)] = alphabet[random.Next(alphabet.Length)];
                    break;
            }
        }

        return string.Concat(parts);
    }

    private static string Show(string text) => text.Replace("\n", "\\n");
}
