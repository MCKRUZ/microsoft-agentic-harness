using System.Text.Json;
using Domain.Common.Compliance;

namespace Application.AI.Common.Interfaces.Compliance;

/// <summary>
/// A store that can return everything it holds for one person, as one section of a personal-data export (#902).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Implemented by the store itself, and registered as this interface wherever the store is registered</strong>
/// (<c>AddPersonalDataExporter</c>). A store whose <c>[PersonalDataStore]</c> descriptor says <c>ExportSupport.ByOwner</c>
/// must implement this, and one that implements it must say so; <c>EveryPersonalDataStoreIsDeclaredTests</c> enforces both
/// directions.
/// </para>
/// <para>
/// <strong>Return only what is the person's.</strong> Filter on the owner form your writer stored
/// (<see cref="ExportSubject.OwnerId"/> or <see cref="ExportSubject.CanonicalOwnerId"/>), never treat a missing owner as a
/// match: a record with no owner is shared or global, and handing it over would disclose someone else's data. Return the
/// records in the store's own shape, including any the person cannot see through the product (a quarantined memory is
/// still theirs).
/// </para>
/// </remarks>
public interface IPersonalDataExporter
{
    /// <summary>
    /// The store's name in the export's manifest, as a reviewer would say it. A store that also erases uses its erasure
    /// receipt name, so the two documents name it the same way.
    /// </summary>
    string ExportStoreName { get; }

    /// <summary>Returns every record the store holds for <paramref name="subject"/>.</summary>
    /// <param name="subject">The person the copy is for.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The person's records, each serialised as JSON.</returns>
    Task<IReadOnlyList<JsonElement>> ExportSubjectAsync(ExportSubject subject, CancellationToken cancellationToken);
}
