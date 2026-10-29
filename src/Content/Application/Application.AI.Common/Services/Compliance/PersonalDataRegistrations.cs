using Application.AI.Common.Interfaces.Compliance;

namespace Application.AI.Common.Services.Compliance;

/// <summary>
/// Collapses the personal-data registrations a host makes twice, so the erasure receipt and the export each read one store
/// once (#1004).
/// </summary>
/// <remarks>
/// <para>
/// A substrate composed twice registers every forwarding registration twice, and each resolves to the same store instance.
/// Read twice, that store would appear twice: two sections of the same records in a person's copy, two receipt lines with
/// the second reading "erased 0". One instance is one store, so instances are compared by reference, never by
/// <see cref="object.Equals(object)"/>, which a store's own type could override to make two stores look like one.
/// </para>
/// <para>
/// A <see cref="NonErasableStore"/> is the exception: <c>AddPersonalDataEraser</c> builds a new stand-in on every
/// resolution, so two registrations of one store that cannot erase are two instances. A stand-in carries its store's type
/// name and nothing else, so stand-ins are collapsed by that name.
/// </para>
/// </remarks>
internal static class PersonalDataRegistrations
{
    /// <summary>
    /// The erasers, each store once: a forwarded registration that resolved to nothing is dropped, the same instance
    /// registered twice is kept once, and stand-ins for one store that cannot erase are kept once.
    /// </summary>
    /// <param name="erasers">The erasers as the container resolved them, in registration order.</param>
    /// <returns>The distinct erasers, in the order each first appeared.</returns>
    public static IReadOnlyList<IPersonalDataEraser> DistinctErasers(IEnumerable<IPersonalDataEraser?> erasers) =>
        erasers.OfType<IPersonalDataEraser>()
            .DistinctBy(e => e is NonErasableStore standIn ? standIn.ErasureStoreName : (object)e, StandInNameOrInstance.Instance)
            .ToList();

    /// <summary>
    /// The exporters, each store once: a forwarded registration that resolved to nothing is dropped, and the same instance
    /// registered twice is kept once.
    /// </summary>
    /// <param name="exporters">The exporters as the container resolved them, in registration order.</param>
    /// <returns>The distinct exporters, in the order each first appeared.</returns>
    public static IReadOnlyList<IPersonalDataExporter> DistinctExporters(IEnumerable<IPersonalDataExporter?> exporters) =>
        exporters.OfType<IPersonalDataExporter>().Distinct<IPersonalDataExporter>(ReferenceEqualityComparer.Instance).ToList();

    /// <summary>Compares a stand-in's store name by value and anything else by reference.</summary>
    private sealed class StandInNameOrInstance : IEqualityComparer<object>
    {
        public static readonly StandInNameOrInstance Instance = new();

        public new bool Equals(object? x, object? y) =>
            x is string a && y is string b ? string.Equals(a, b, StringComparison.Ordinal) : ReferenceEquals(x, y);

        public int GetHashCode(object obj) =>
            obj is string name ? StringComparer.Ordinal.GetHashCode(name) : ReferenceEqualityComparer.Instance.GetHashCode(obj);
    }
}
