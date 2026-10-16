using System.Text.RegularExpressions;
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
            var body = $"passed: {(rollout.Passed ? "true" : "false")}\nitem: {rollout.ItemId}\n\n{rollout.Trajectory}";
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

    private static string SafeName(string itemId)
    {
        var cleaned = UnsafeFileNameChars().Replace(itemId, "_").Trim('_', '.');
        if (cleaned.Length == 0)
            return "item";

        return cleaned.Length <= 60 ? cleaned : cleaned[..60];
    }
}
