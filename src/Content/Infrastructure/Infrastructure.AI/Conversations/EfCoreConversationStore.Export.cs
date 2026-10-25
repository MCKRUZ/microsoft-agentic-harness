using System.Text.Json;
using Application.AI.Common.Interfaces.Compliance;
using Domain.Common.Compliance;

namespace Infrastructure.AI.Conversations;

/// <summary>Personal-data export (#902): the person's conversations, with every message.</summary>
public sealed partial class EfCoreConversationStore : IPersonalDataExporter
{
    /// <inheritdoc />
    public string ExportStoreName => ErasureStoreName;

    /// <summary>
    /// Every conversation the person owns, matched on the owner exactly as issued, the form this store records and the
    /// form its erasure matches.
    /// </summary>
    /// <param name="subject">The person the copy is for.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Each conversation as stored, with its messages.</returns>
    public async Task<IReadOnlyList<JsonElement>> ExportSubjectAsync(ExportSubject subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return ConversationJson.ToExportRecords(await ListAsync(subject.OwnerId, cancellationToken));
    }
}
