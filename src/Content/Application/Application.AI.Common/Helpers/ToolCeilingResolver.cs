namespace Application.AI.Common.Helpers;

/// <summary>
/// Resolves the effective tool allowlist for an agent by applying its declared tool <em>ceiling</em>
/// to the allowlist its skills already grant. This is the single, security-critical primitive behind
/// the agent tool ceiling: a ceiling can only ever <em>tighten</em> the set of tools an agent may
/// invoke — it can never widen it.
/// </summary>
/// <remarks>
/// <para>
/// An agent's <c>allowed-tools</c> frontmatter is a request for an upper bound, not a grant. The tools
/// an agent can actually use are the ones its skills contribute; the ceiling can remove tools from that
/// set but never add one the skills did not already permit. This mirrors how a bundle's self-declared
/// capabilities are only <em>requests</em> against a host-granted envelope — the same tighten-only
/// posture applied one level down, at the agent/skill boundary.
/// </para>
/// <para>
/// A bare <c>IReadOnlyList&lt;string&gt;</c> cannot distinguish "no restriction at all" from "a
/// restriction that happens to permit nothing", and conflating the two is a privilege-escalation bug:
/// once an intersection has narrowed to nothing, a later ceiling must NOT be able to re-grant tools.
/// So this primitive uses <see langword="null"/> for <em>unbounded</em> (no restriction) and a non-null
/// list — <em>including an empty one</em> — for <em>bounded</em> (empty = deny all). With that
/// distinction the operation is a monotone intersection that is always safe to chain: applying more
/// ceilings can only ever shrink the set.
/// </para>
/// </remarks>
public static class ToolCeilingResolver
{
    /// <summary>
    /// Caps <paramref name="current"/> by an optional tool <paramref name="ceiling"/>, tighten-only.
    /// </summary>
    /// <param name="current">
    /// The allowlist granted so far. <see langword="null"/> means <em>unbounded</em> (no restriction —
    /// every tool is permitted); a non-null list is an active restriction, and an empty one denies all.
    /// </param>
    /// <param name="ceiling">
    /// The ceiling to apply. When null or empty the ceiling is absent and <paramref name="current"/> is
    /// returned unchanged.
    /// </param>
    /// <returns>
    /// <list type="bullet">
    ///   <item><description>
    ///     <paramref name="current"/> unchanged when the ceiling is null or empty (no ceiling declared).
    ///   </description></item>
    ///   <item><description>
    ///     the ceiling (de-duplicated) when <paramref name="current"/> is <see langword="null"/> — a
    ///     ceiling caps an otherwise-unbounded allowlist.
    ///   </description></item>
    ///   <item><description>
    ///     otherwise the intersection of the two (a subset of <paramref name="current"/>, order
    ///     preserved). An empty intersection is a genuine deny-all and is preserved, so chaining another
    ///     ceiling onto it can never re-grant a tool.
    ///   </description></item>
    /// </list>
    /// Comparison is case-insensitive and the result is de-duplicated.
    /// </returns>
    public static IReadOnlyList<string>? ApplyCeiling(
        IReadOnlyList<string>? current,
        IReadOnlyList<string>? ceiling)
    {
        // No ceiling declared: whatever restriction `current` carries (including none) stands unchanged.
        if (ceiling is null || ceiling.Count == 0)
            return current;

        // Unbounded so far: the ceiling alone becomes the allowlist (a tightening from "everything").
        if (current is null)
            return ceiling.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Both bounded: intersect. The result is always a subset of `current`, so the ceiling can never
        // grant a tool the current allowlist did not already permit. An empty result stays empty
        // (deny all) and remains a subset under any further ceiling — the invariant that makes chaining safe.
        var cap = new HashSet<string>(ceiling, StringComparer.OrdinalIgnoreCase);
        return current
            .Where(cap.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
