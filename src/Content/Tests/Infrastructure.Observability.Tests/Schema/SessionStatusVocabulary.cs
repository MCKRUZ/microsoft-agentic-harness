using Domain.AI.Observability.Models;

namespace Infrastructure.Observability.Tests.Schema;

/// <summary>
/// The statuses the code can actually persist — the one definition every agreement test in this
/// folder measures its artifact against.
/// </summary>
/// <remarks>
/// Extracted because two suites had begun deriving it separately. That is a small duplication with a
/// nasty shape: if <c>ToDbValue</c> ever needs an exclusion — a status the code can express but never
/// writes — one suite would get it and the other would go on asserting the old set, and these are the
/// only guards on this vocabulary that cannot be skipped.
/// </remarks>
internal static class SessionStatusVocabulary
{
    /// <summary>Every status the code can write, as the schema stores it.</summary>
    public static IReadOnlyList<string> Writable { get; } =
        [.. Enum.GetValues<SessionStatus>()
               .Select(s => s.ToDbValue())
               .OrderBy(v => v, StringComparer.Ordinal)];

    /// <summary>The same set, ordered, for comparison against an artifact's values.</summary>
    /// <param name="values">Values read out of a checked-in artifact.</param>
    /// <returns>The values in the same order <see cref="Writable"/> uses.</returns>
    public static IEnumerable<string> Ordered(IEnumerable<string> values) =>
        values.OrderBy(v => v, StringComparer.Ordinal);
}
