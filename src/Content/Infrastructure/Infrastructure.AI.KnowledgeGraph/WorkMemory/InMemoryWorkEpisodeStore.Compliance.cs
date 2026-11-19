using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Interfaces.Compliance;
using Domain.AI.KnowledgeGraph.Scoping;
using Domain.AI.WorkMemory;
using Domain.Common.Compliance;

namespace Infrastructure.AI.KnowledgeGraph.WorkMemory;

/// <summary>
/// Right-to-erasure and export for the in-memory episode store (#968): once an episode carries its owner, the person's
/// episodes are theirs to remove and to receive. The graph-backed store needs neither, because its episodes are graph
/// nodes stamped with the owner, which the graph's own erasure and export reach.
/// </summary>
public sealed partial class InMemoryWorkEpisodeStore : IPersonalDataEraser, IPersonalDataExporter
{
    private static readonly JsonSerializerOptions ExportJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <inheritdoc />
    public string ErasureStoreName => "work-memory episodes";

    /// <inheritdoc />
    /// <remarks>Nothing else is found through an episode, so it can go before the conversations it came from.</remarks>
    public ErasureStage ErasureStage => ErasureStage.BeforeConversations;

    /// <inheritdoc />
    public ErasureReach ErasureReach => ErasureReach.Owner;

    /// <inheritdoc />
    public string ExportStoreName => ErasureStoreName;

    /// <inheritdoc />
    /// <remarks>Every tenant, because erasure is about the person, not the tenant an episode was saved under.</remarks>
    public Task<int> EraseSubjectAsync(ErasureSubject subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var removed = OwnedBy(subject.OwnerId).Count(e => _episodes.TryRemove(e.EpisodeId, out _));
        return Task.FromResult(removed);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<JsonElement>> ExportSubjectAsync(ExportSubject subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        IReadOnlyList<JsonElement> records =
            [.. OwnedBy(subject.OwnerId).Select(e => JsonSerializer.SerializeToElement(e, ExportJson))];
        return Task.FromResult(records);
    }

    private List<WorkEpisode> OwnedBy(string ownerId) =>
        [.. _episodes.Values.Where(e => e.OwnerId is not null && ScopeIdentity.AreSame(e.OwnerId, ownerId))];
}
