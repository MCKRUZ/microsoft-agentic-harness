namespace Domain.AI.Retrieval;

/// <summary>
/// Generic Reciprocal Rank Fusion (RRF): merges any number of independently-ranked lists into one ranked
/// list by summing each item's <c>1 / (k + rank)</c> contribution across the lists in which it appears.
/// RRF needs only the <em>rank</em> of an item in each list, not comparable per-list scores, which is what
/// makes it the standard way to fuse heterogeneous retrievers (dense + sparse, lexical + semantic, …).
/// </summary>
/// <remarks>
/// <para>
/// Items are matched across lists by a caller-supplied string key. Ordering is deterministic: entries are
/// ranked by fused score descending, and ties are broken by first appearance (an item seen earlier, in an
/// earlier list or at a lower rank, sorts first) — so the same inputs always yield the same output, which
/// keeps fusion testable.
/// </para>
/// <para>
/// This is the single RRF primitive in the codebase; the hybrid RAG retriever and the harmonic memory
/// recall path both fuse through it. It lives in the domain layer because it is a pure, dependency-free
/// algorithm over abstract ranked lists — no retrieval, AI, or storage types leak in.
/// </para>
/// </remarks>
public static class ReciprocalRankFusion
{
    /// <summary>
    /// The default RRF constant. Higher values flatten the influence of top ranks (a large <c>k</c> makes
    /// rank 1 and rank 10 nearly equal); 60 is the value from the original Cormack et al. RRF paper and the
    /// default across this codebase's retrievers.
    /// </summary>
    public const double DefaultK = 60.0;

    /// <summary>
    /// Fuses the given ranked lists into a single ranked result.
    /// </summary>
    /// <typeparam name="T">The ranked item type.</typeparam>
    /// <param name="rankedLists">The input lists, each already ordered best-first. Empty lists are allowed
    /// and contribute nothing. The positional order of the lists is preserved in
    /// <see cref="FusedResult{T}.PerListItems"/>.</param>
    /// <param name="keySelector">Extracts the fusion key that identifies "the same item" across lists.
    /// Items with equal keys are fused; items with distinct keys stay distinct.</param>
    /// <param name="k">The RRF constant (defaults to <see cref="DefaultK"/>). Must be positive.</param>
    /// <param name="topK">Optional cap on the number of returned entries. <see langword="null"/> (the
    /// default) returns every fused entry.</param>
    /// <returns>The fused entries, ranked by descending score with a deterministic first-appearance
    /// tiebreak.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rankedLists"/> or
    /// <paramref name="keySelector"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="k"/> is not positive, or
    /// <paramref name="topK"/> is negative.</exception>
    public static IReadOnlyList<FusedResult<T>> Fuse<T>(
        IReadOnlyList<IReadOnlyList<T>> rankedLists,
        Func<T, string> keySelector,
        double k = DefaultK,
        int? topK = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(rankedLists);
        ArgumentNullException.ThrowIfNull(keySelector);
        if (k <= 0)
            throw new ArgumentOutOfRangeException(nameof(k), k, "The RRF constant k must be positive.");
        if (topK is < 0)
            throw new ArgumentOutOfRangeException(nameof(topK), topK, "topK must not be negative.");

        var listCount = rankedLists.Count;

        // Accumulate per key, remembering first-appearance order for a deterministic tiebreak and each
        // list's contribution so callers can reconstruct a per-source-enriched output.
        var accumulators = new Dictionary<string, Accumulator<T>>(StringComparer.Ordinal);
        var order = 0;

        for (var listIndex = 0; listIndex < listCount; listIndex++)
        {
            var list = rankedLists[listIndex];
            for (var rank = 0; rank < list.Count; rank++)
            {
                var item = list[rank];
                var key = keySelector(item);
                var rrfScore = 1.0 / (k + rank + 1);

                if (!accumulators.TryGetValue(key, out var acc))
                {
                    acc = new Accumulator<T>(item, order++, new T?[listCount]);
                    accumulators[key] = acc;
                }

                // Each list contributes to a key at most once, at its best (first-seen) rank. Lists are
                // ordered best-first, so a key repeated later in the same list is an inferior rank and must
                // not double-count — counting it would inflate the item's fused score. A key appearing in
                // several distinct lists still accumulates one contribution per list, which is the point of
                // fusion.
                if (acc.PerListItems[listIndex] is null)
                {
                    acc.PerListItems[listIndex] = item;
                    acc.Score += rrfScore;
                }
            }
        }

        var ranked = accumulators.Values
            .OrderByDescending(a => a.Score)
            .ThenBy(a => a.Order)
            .Select(a => new FusedResult<T>(a.Representative, a.Score, a.PerListItems));

        if (topK is int cap)
            ranked = ranked.Take(cap);

        return ranked.ToList();
    }

    private sealed class Accumulator<T>(T representative, int order, T?[] perListItems)
        where T : class
    {
        public T Representative { get; } = representative;
        public int Order { get; } = order;
        public T?[] PerListItems { get; } = perListItems;
        public double Score { get; set; }
    }
}
