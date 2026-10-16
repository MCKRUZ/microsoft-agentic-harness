using System.Text.RegularExpressions;
using Application.AI.Common.Services.SkillTraining;
using Application.Common.Interfaces.Common;
using Domain.AI.SkillTraining;

namespace Infrastructure.AI.SkillTraining;

/// <summary>
/// Writes one training step's evidence to disk for the proposer session to read (#843).
/// </summary>
/// <remarks>
/// <para>
/// The evidence is served as files, not pasted into the prompt, so nothing is clipped to fit a context:
/// a long trajectory is read in pages by the confined <c>evidence_reader</c> tool. The prompt is a short
/// pointer to the folder.
/// </para>
/// <code>
/// {root}/{id}/skill/SKILL.md           the skill the rollouts were produced against
/// {root}/{id}/rollouts/NNN-item.md     "passed: true|false", the item id, then the full trajectory
/// {root}/{id}/memory/EXPERIENCE.md     cross-epoch memory, when there is any
/// </code>
/// <para>
/// Every directory is created owner-only: a trajectory is data the agent saw and produced, and the
/// folder is not for anyone else on the machine. The tree is removed when the step is done.
/// </para>
/// </remarks>
internal static partial class ProposerEvidenceTree
{
    [GeneratedRegex(@"[^A-Za-z0-9._-]+")]
    private static partial Regex UnsafeFileNameChars();

    /// <summary>
    /// The most characters of one trajectory written to disk. The reader's file service refuses files over
    /// 10 MB, and a trajectory past that would be unreadable rather than long; at three bytes a character
    /// this stays under it.
    /// </summary>
    public const int MaxTrajectoryChars = 3_000_000;

    /// <summary>A written tree: where it is and what it holds.</summary>
    /// <param name="Directory">Absolute directory of this step's tree.</param>
    /// <param name="RolloutCount">Rollout files written.</param>
    /// <param name="PassedCount">Of those, how many passed.</param>
    /// <param name="HasMemory">Whether <c>memory/EXPERIENCE.md</c> was written.</param>
    public sealed record Written(string Directory, int RolloutCount, int PassedCount, bool HasMemory);

    /// <summary>Writes the tree for <paramref name="input"/> under <paramref name="root"/>.</summary>
    /// <param name="root">The absolute evidence root.</param>
    /// <param name="input">The proposer's input.</param>
    /// <param name="directories">Creates directories owner-only.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public static async Task<Written> WriteAsync(
        string root,
        ReflectionInput input,
        IOwnerOnlyDirectoryCreator directories,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        var skillDir = Path.Combine(directory, "skill");
        var rolloutDir = Path.Combine(directory, "rollouts");
        directories.Create(skillDir);
        directories.Create(rolloutDir);

        await File.WriteAllTextAsync(Path.Combine(skillDir, "SKILL.md"), input.CurrentSkill, cancellationToken)
            .ConfigureAwait(false);

        var shown = input.Rollouts.Where(r => input.IncludeSuccesses || !r.Passed).ToList();
        for (var i = 0; i < shown.Count; i++)
        {
            var rollout = shown[i];
            var name = $"{i + 1:D3}-{SafeName(rollout.ItemId)}.md";
            var body = $"passed: {(rollout.Passed ? "true" : "false")}\nitem: {rollout.ItemId}\n\n{Bounded(rollout.Trajectory)}";
            await File.WriteAllTextAsync(Path.Combine(rolloutDir, name), body, cancellationToken).ConfigureAwait(false);
        }

        var hasMemory = !string.IsNullOrWhiteSpace(input.MetaSkillMemory);
        if (hasMemory)
        {
            var memoryDir = Path.Combine(directory, "memory");
            directories.Create(memoryDir);
            await File.WriteAllTextAsync(Path.Combine(memoryDir, "EXPERIENCE.md"), input.MetaSkillMemory, cancellationToken)
                .ConfigureAwait(false);
        }

        return new Written(directory, shown.Count, shown.Count(r => r.Passed), hasMemory);
    }

    /// <summary>The outcome of <see cref="ScreenAsync"/>.</summary>
    /// <param name="Tree">The tree as it stands after leaking rollouts were removed.</param>
    /// <param name="Findings">Every grader-verdict marker found, with where.</param>
    /// <param name="Usable">Whether a proposer session should go ahead on what is left.</param>
    public sealed record Screened(Written Tree, IReadOnlyList<GraderLeakFinding> Findings, bool Usable);

    /// <summary>
    /// Scans what was written, file by file, for grader-verdict markers. A rollout that carries one is
    /// deleted and the rest stand; a marker in the skill or the memory file makes the step unusable, as it
    /// does in the loop. A tree that had rollouts and has none left is unusable too.
    /// </summary>
    /// <param name="tree">The tree to screen.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    public static async Task<Screened> ScreenAsync(Written tree, CancellationToken cancellationToken)
    {
        var findings = new List<GraderLeakFinding>();
        var fixedFiles = new[]
        {
            Path.Combine(tree.Directory, "skill", "SKILL.md"),
            Path.Combine(tree.Directory, "memory", "EXPERIENCE.md"),
        };

        var fatal = false;
        foreach (var file in fixedFiles.Where(File.Exists))
        {
            var text = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            var found = GraderMarkerScanner.Scan([(RelativeName(tree.Directory, file), text)]);
            findings.AddRange(found);
            fatal |= found.Count > 0;
        }

        var kept = 0;
        var passed = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(tree.Directory, "rollouts")).Order(StringComparer.Ordinal))
        {
            var text = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            var found = GraderMarkerScanner.Scan([(RelativeName(tree.Directory, file), text)]);
            if (found.Count > 0)
            {
                findings.AddRange(found);
                File.Delete(file);
                continue;
            }

            kept++;
            if (text.StartsWith("passed: true", StringComparison.Ordinal))
                passed++;
        }

        var usable = !fatal && (tree.RolloutCount == 0 || kept > 0);
        return new Screened(tree with { RolloutCount = kept, PassedCount = passed }, findings, usable);
    }

    /// <summary>Removes a tree written by <see cref="WriteAsync"/>; never throws.</summary>
    /// <param name="directory">The tree's directory.</param>
    public static void Delete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a tree that cannot be removed now is removed with the root, and failing the step over it would be worse.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// A trajectory over <see cref="MaxTrajectoryChars"/> keeps its beginning and its end, with a line
    /// saying how much was left out of the middle, rather than being unreadable whole.
    /// </summary>
    private static string Bounded(string trajectory)
    {
        if (trajectory.Length <= MaxTrajectoryChars)
            return trajectory;

        var half = MaxTrajectoryChars / 2;
        var omitted = trajectory.Length - 2 * half;
        return trajectory[..half]
            + $"\n\n[... {omitted} characters omitted from the middle of this run: it was too long to store whole ...]\n\n"
            + trajectory[^half..];
    }

    private static string RelativeName(string directory, string file) =>
        Path.GetRelativePath(directory, file).Replace('\\', '/');

    private static string SafeName(string itemId)
    {
        var cleaned = UnsafeFileNameChars().Replace(itemId, "_").Trim('_', '.');
        if (cleaned.Length == 0)
            return "item";

        return cleaned.Length <= 60 ? cleaned : cleaned[..60];
    }
}
