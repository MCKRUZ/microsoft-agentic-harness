namespace Application.AI.Common.Interfaces.Skills;

/// <summary>
/// Ambient accessor that exposes the identifiers of the skill(s) currently driving
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
/// An empty list means "no skill active" — resolvers fall back to the
/// harness-wide default policy. More than one id means the current tool call is
/// shared by multiple skills (#589) — a policy resolver that varies by skill must
/// union each named skill's own contribution rather than picking one. Implementations
/// are thread-safe; concurrent agent turns running on different async contexts each
/// see their own value.
/// </para>
/// </remarks>
public interface ICurrentSkillAccessor
{
    /// <summary>
    /// Gets the identifiers of the skill(s) currently active on this async context,
    /// or an empty list when no skill scope has been established.
    /// </summary>
    IReadOnlyList<string> CurrentSkillIds { get; }

    /// <summary>
    /// Establishes the supplied <paramref name="skillIds"/> as the current skill(s)
    /// for this async context until the returned token is disposed. Restores
    /// the previous value on disposal so nested skill activations compose.
    /// </summary>
    /// <param name="skillIds">
    /// The identifier(s) of the skill(s) to make current. Must not be null or empty, and no entry
    /// may be null or whitespace.
    /// </param>
    /// <returns>A token that restores the previous current-skill value when disposed.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="skillIds"/> is null, empty, or contains a null/whitespace entry.
    /// </exception>
    IDisposable BeginScope(IReadOnlyList<string> skillIds);
}
