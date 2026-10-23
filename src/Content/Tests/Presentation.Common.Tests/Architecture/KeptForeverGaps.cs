namespace Presentation.Common.Tests.Architecture;

/// <summary>
/// The stores that still declare <c>RetentionBasis.Unbounded</c>: nothing deletes what they hold (#901). Read by
/// <see cref="EveryPersonalDataStoreIsDeclaredTests"/>.
/// </summary>
/// <remarks>
/// This list only shrinks. A new store may not join it, and a store that gains a retention period, or a written reason to
/// be kept, must leave it, or the guard fails. Each entry says what will close the gap.
/// </remarks>
internal static class KeptForeverGaps
{
    /// <summary>The gaps, by full type name, with what closes each one.</summary>
    public static readonly IReadOnlyDictionary<string, string> Entries = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // Empty since #901 closed: every store has a retention period or a written reason to be kept. Keep it empty.
    };
}
