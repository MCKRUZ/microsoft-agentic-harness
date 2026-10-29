using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Interfaces.Compliance;
using Domain.Common.Compliance;

namespace Infrastructure.AI.Bundles;

/// <summary>Personal-data export (#990): the person's bundle runs.</summary>
public sealed partial class InMemoryBundleRunJobStore : IPersonalDataExporter
{
    private static readonly JsonSerializerOptions ExportJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <inheritdoc />
    public string ExportStoreName => ErasureStoreName;

    /// <summary>
    /// Every bundle run the person started, finished or not, matched the way erasure matches: a run in flight is still
    /// theirs, even though erasure keeps it until it ends. Each record holds the messages they sent and the agent's final
    /// answer, so the copy includes them.
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
