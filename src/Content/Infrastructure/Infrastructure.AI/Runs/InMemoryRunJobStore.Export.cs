using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Interfaces.Compliance;
using Domain.Common.Compliance;

namespace Infrastructure.AI.Runs;

/// <summary>Personal-data export (#926): the person's runs.</summary>
public sealed partial class InMemoryRunJobStore : IPersonalDataExporter
{
    private static readonly JsonSerializerOptions ExportJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <inheritdoc />
    public string ExportStoreName => ErasureStoreName;

    /// <summary>
    /// Every run the person owns, in every tenant, finished or not, matched the way erasure matches: a live run is still
    /// theirs, even though erasure keeps it until it ends.
    /// </summary>
    /// <param name="subject">The person the copy is for.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Each run record as the store holds it.</returns>
    public Task<IReadOnlyList<JsonElement>> ExportSubjectAsync(ExportSubject subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        IReadOnlyList<JsonElement> records =
            [.. OwnedBy(subject.OwnerId).Select(r => JsonSerializer.SerializeToElement(r, ExportJson))];
        return Task.FromResult(records);
    }
}
