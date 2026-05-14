using Domain.AI.Skills;

namespace Application.AI.Common.Interfaces.Skills;

/// <summary>
/// Records and queries skill effectiveness per query classification. Used by the
/// orchestration layer to inform skill selection based on historical performance.
/// </summary>
public interface ISkillEffectivenessTracker
{
    /// <summary>Record an outcome for a skill invocation.</summary>
    Task RecordOutcomeAsync(
        string skillId,
        string queryClassification,
        bool succeeded,
        double? qualityScore = null,
        CancellationToken cancellationToken = default);

    /// <summary>Get the most effective skills for a query classification, ranked by success rate.</summary>
    Task<IReadOnlyList<SkillEffectivenessRecord>> GetEffectivenessAsync(
        string queryClassification,
        int topN = 5,
        CancellationToken cancellationToken = default);
}
