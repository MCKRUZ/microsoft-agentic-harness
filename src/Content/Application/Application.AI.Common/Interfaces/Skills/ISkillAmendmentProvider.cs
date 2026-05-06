using Domain.AI.Skills;

namespace Application.AI.Common.Interfaces.Skills;

/// <summary>
/// Manages learned instruction amendments for skills. Amendments are stored in the
/// knowledge graph and loaded alongside skill instructions at Tier 2.
/// </summary>
public interface ISkillAmendmentProvider
{
    /// <summary>Get all amendments for a skill, ordered by creation date.</summary>
    Task<IReadOnlyList<SkillAmendment>> GetAmendmentsAsync(
        string skillId,
        CancellationToken cancellationToken = default);

    /// <summary>Add a new amendment to a skill.</summary>
    Task AddAmendmentAsync(
        SkillAmendment amendment,
        CancellationToken cancellationToken = default);

    /// <summary>Remove an amendment by its ID.</summary>
    Task RemoveAmendmentAsync(
        string amendmentId,
        CancellationToken cancellationToken = default);
}
