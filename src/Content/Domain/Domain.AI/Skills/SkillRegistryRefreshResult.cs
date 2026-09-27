namespace Domain.AI.Skills;

/// <summary>
/// What a <see cref="SkillDefinition"/> registry reload actually changed, compared to the
/// picture it held immediately before the reload ran.
/// </summary>
/// <remarks>
/// An operator triggering a manual reload is asking a concrete question — "did my edit take?" —
/// and a bare success/failure answers a different one. Diffing old against new lets the response
/// name exactly which skill ids appeared, disappeared, or were replaced, rather than making the
/// caller re-list everything and compare by hand (issue #709, mirroring
/// <c>Domain.AI.Agents.AgentRegistryRefreshResult</c> from issue #705).
/// </remarks>
public sealed record SkillRegistryRefreshResult
{
    /// <summary>Skill ids present after the reload that were not present before it.</summary>
    public required IReadOnlyList<string> Added { get; init; }

    /// <summary>
    /// Skill ids present both before and after the reload whose definition changed (a different
    /// name, description, instructions, or any other identity/behaviour field on
    /// <see cref="SkillDefinition"/>).
    /// </summary>
    public required IReadOnlyList<string> Updated { get; init; }

    /// <summary>Skill ids present before the reload that are no longer present after it.</summary>
    public required IReadOnlyList<string> Removed { get; init; }

    /// <summary>Total skill count after the reload.</summary>
    public required int TotalSkillCount { get; init; }

    /// <summary>The filesystem paths searched during this reload.</summary>
    public required IReadOnlyList<string> SearchedPaths { get; init; }
}
