using Application.AI.Common.Interfaces.Skills;

namespace Infrastructure.AI.Skills;

/// <summary>
/// Serves skill content from an in-memory snapshot of a HarnessCandidate.
/// Used during evaluation to isolate candidate skill content from the active filesystem state.
/// Returns null for paths not present in the snapshot.
/// </summary>
public sealed class CandidateSkillContentProvider : ISkillContentProvider
{
    private readonly IReadOnlyDictionary<string, string> _skillFileSnapshots;

    /// <param name="skillFileSnapshots">
    /// Map of skill file path → content from the candidate's snapshot.
    /// Typically <c>HarnessCandidate.Snapshot.SkillFileSnapshots</c>.
    /// Lookup is case-insensitive to handle Windows path casing differences.
    /// </param>
    public CandidateSkillContentProvider(IReadOnlyDictionary<string, string> skillFileSnapshots)
    {
        ArgumentNullException.ThrowIfNull(skillFileSnapshots);
        // Normalize to case-insensitive to handle Windows path casing differences
        _skillFileSnapshots = new Dictionary<string, string>(
            skillFileSnapshots, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public Task<string?> GetSkillContentAsync(string skillPath, CancellationToken cancellationToken = default)
    {
        _skillFileSnapshots.TryGetValue(skillPath, out var content);
        return Task.FromResult(content);
    }
}
