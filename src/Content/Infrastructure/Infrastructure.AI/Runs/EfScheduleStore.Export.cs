using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Interfaces.Compliance;
using Domain.Common.Compliance;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.AI.Runs;

/// <summary>Personal-data export (#902): the person's schedules.</summary>
public sealed partial class EfScheduleStore : IPersonalDataExporter
{
    private static readonly JsonSerializerOptions ExportJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <inheritdoc />
    public string ExportStoreName => ErasureStoreName;

    /// <summary>
    /// Every schedule the person owns, in every tenant, matched on <see cref="ExportSubject.CanonicalOwnerId"/>: the form
    /// schedules are stored in and the one erasure matches.
    /// </summary>
    /// <param name="subject">The person the copy is for.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Each schedule as the store returns it to its owner.</returns>
    public async Task<IReadOnlyList<JsonElement>> ExportSubjectAsync(ExportSubject subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);

        await using var ctx = await _factory.CreateDbContextAsync(cancellationToken);
        var entities = await ctx.Schedules
            .AsNoTracking()
            .Where(e => e.OwnerId == subject.CanonicalOwnerId)
            .ToListAsync(cancellationToken);
        return [.. entities.Select(e => JsonSerializer.SerializeToElement(ToRecord(e), ExportJson))];
    }
}
