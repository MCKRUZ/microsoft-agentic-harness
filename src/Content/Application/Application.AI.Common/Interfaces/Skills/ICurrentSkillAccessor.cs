namespace Application.AI.Common.Interfaces.Skills;

/// <summary>
/// Ambient accessor that exposes the identifier of the skill currently driving
/// the agent turn. Set by the skill-execution path when a skill activates,
/// cleared when it deactivates. Consumed by per-skill policy resolvers (e.g.
/// the egress allowlist resolver) that need to vary behavior by skill without
/// threading the skill identifier through every method call.
/// </summary>
/// <remarks>
/// <para>
/// The accessor uses <see cref="System.Threading.AsyncLocal{T}"/> so the value
/// flows down the async call chain into delegating handlers, MediatR pipeline
/// behaviors, and background continuations launched within the skill's logical
/// scope.
/// </para>
/// <para>
/// A null value means "no skill active" — resolvers fall back to the
/// harness-wide default policy. Implementations are thread-safe; concurrent
/// agent turns running on different async contexts each see their own value.
/// </para>
/// </remarks>
public interface ICurrentSkillAccessor
{
    /// <summary>
    /// Gets the identifier of the skill currently active on this async context,
    /// or null when no skill scope has been established.
    /// </summary>
    string? CurrentSkillId { get; }

    /// <summary>
    /// Establishes the supplied <paramref name="skillId"/> as the current skill
    /// for this async context until the returned token is disposed. Restores
    /// the previous value on disposal so nested skill activations compose.
    /// </summary>
    /// <param name="skillId">The identifier of the skill to make current. Must not be null or whitespace.</param>
    /// <returns>A token that restores the previous current-skill value when disposed.</returns>
    /// <exception cref="ArgumentException">The supplied <paramref name="skillId"/> is null, empty, or whitespace.</exception>
    IDisposable BeginScope(string skillId);
}
