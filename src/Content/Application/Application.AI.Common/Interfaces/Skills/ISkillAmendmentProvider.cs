using Domain.AI.Skills;

namespace Application.AI.Common.Interfaces.Skills;

/// <summary>
/// Manages learned instruction amendments for skills. Amendments are stored in the
/// knowledge graph and loaded alongside skill instructions at Tier 2.
/// </summary>
/// <remarks>
/// Amendment content reaches the system prompt verbatim and unsanitized —
/// <c>SkillInstructionMerger.Merge</c> appends <see cref="SkillAmendment.Content"/>
/// straight into the agent's instructions, the same trust level as the skill's own author-written body.
/// No writer of <see cref="AddAmendmentAsync"/> exists in this template today (every call site is a
/// test), so this is not currently reachable by anything other than a trusted operator — but a future
/// writer that lets an LLM or an untrusted party propose amendment text (e.g. a skill-training loop, or
/// exposing this over an API) must scan/sanitize before calling <see cref="AddAmendmentAsync"/>, the
/// same way <c>IMemoryWriteGate</c> screens every other piece of learned content before it is trusted.
/// </remarks>
public interface ISkillAmendmentProvider
{
    /// <summary>Get all amendments for a skill, ordered by creation date.</summary>
    Task<IReadOnlyList<SkillAmendment>> GetAmendmentsAsync(
        string skillId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Add a new amendment to a skill. Callers must ensure <paramref name="amendment"/>'s
    /// <see cref="SkillAmendment.Content"/> is trustworthy before calling — see the interface remarks.
    /// </summary>
    Task AddAmendmentAsync(
        SkillAmendment amendment,
        CancellationToken cancellationToken = default);

    /// <summary>Remove an amendment by its ID.</summary>
    Task RemoveAmendmentAsync(
        string amendmentId,
        CancellationToken cancellationToken = default);
}
