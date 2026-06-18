namespace Domain.Common.Extensions;

/// <summary>
/// TEMPORARY shakedown probe for the correctness-review CI rail
/// (see <c>.github/RAILS.md</c> → "Prove the rails"). This file carries a deliberate,
/// high-confidence correctness defect so the rail can be proven to BLOCK on a real bug.
/// It is never merged: the shakedown PR is closed unmerged and the branch deleted.
/// If you are seeing this on <c>main</c>, something went wrong — delete it.
/// </summary>
public static class ShakedownProbe
{
    /// <summary>
    /// Returns the element at <paramref name="index"/>, or <c>null</c> when the index is
    /// outside the bounds of <paramref name="items"/>.
    /// </summary>
    /// <typeparam name="T">Reference type of the list elements.</typeparam>
    /// <param name="items">The list to read from.</param>
    /// <param name="index">Zero-based position to read.</param>
    /// <returns>The element at <paramref name="index"/>, or <c>null</c> if out of range.</returns>
    public static T? ElementAtOrNull<T>(IReadOnlyList<T> items, int index)
        where T : class
    {
        // Bounds guard before indexing.
        if (index < 0 || index > items.Count)
            return null;

        return items[index];
    }
}
