namespace Domain.AI.Retrieval;

/// <summary>
/// One entry in a <see cref="ReciprocalRankFusion"/> result: a representative item, its fused score, and the
/// per-input-list items that shared the entry's fusion key.
/// </summary>
/// <typeparam name="T">The ranked item type.</typeparam>
/// <param name="Item">The representative item — the one from the earliest input list that carried this
/// fusion key. Callers that only need the fused ranking can read this and ignore the rest.</param>
/// <param name="Score">The fused Reciprocal Rank Fusion score (higher ranks first).</param>
/// <param name="PerListItems">The contribution from each input list, positionally aligned to the
/// <c>rankedLists</c> argument: <c>PerListItems[i]</c> is the item list <c>i</c> contributed under this
/// fusion key, or <see langword="null"/> when list <c>i</c> did not contain the key. Lets a caller
/// reconstruct a per-source-enriched output (e.g. keep a dense score from one list and a sparse score from
/// another) without re-scanning the inputs.</param>
public sealed record FusedResult<T>(T Item, double Score, IReadOnlyList<T?> PerListItems)
    where T : class;
