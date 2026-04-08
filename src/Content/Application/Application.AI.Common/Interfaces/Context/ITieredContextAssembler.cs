using Application.AI.Common.Models.Context;
using Domain.AI.Skills;

namespace Application.AI.Common.Interfaces.Context;

/// <summary>
/// Assembles context from all three progressive disclosure tiers for a skill,
/// respecting per-tier token budgets.
/// </summary>
/// <remarks>
/// <para>
/// Tier 1 (organizational context) is always loaded and required for skill execution.
/// Tier 2 (domain-specific context) is loaded on demand with truncation tracking.
/// Tier 3 (on-demand lookup) is never pre-loaded — only path configuration is returned.
/// </para>
/// <para>
/// The assembler reads file content from the skill's loaded resources (Templates, References)
/// and matches them against the file lists declared in <see cref="ContextLoading"/> tiers.
/// </para>
/// </remarks>
public interface ITieredContextAssembler
{
    /// <summary>
    /// Assembles context from all tiers for a skill definition.
    /// </summary>
    /// <param name="skill">The skill definition with context loading configuration.</param>
    /// <param name="basePath">Optional base path for resolving relative file references.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The assembled context with per-tier tracking.</returns>
    Task<AssembledContext> AssembleContextAsync(
        SkillDefinition skill,
        string? basePath = null,
        CancellationToken cancellationToken = default);
}
