using System.Globalization;
using System.Text.Json;
using Application.AI.Common.Interfaces.Compliance;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.KnowledgeGraph.Scoping;
using Domain.Common.Compliance;

namespace Infrastructure.AI.RAG.GraphRag;

/// <summary>Personal-data export (#902): the person's cross-session memories, from the cache and the durable backend.</summary>
public sealed partial class CrossSessionMemoryStore : IPersonalDataExporter
{
    /// <inheritdoc />
    public string ExportStoreName => "cross-session memories";

    /// <summary>
    /// Every memory the person owns, from both places erasure purges: the in-process cache and the durable graph backend
    /// (the SQLite <c>graph.db</c> of <c>KuzuGraphBackend</c> by default), matched on
    /// <see cref="ExportSubject.CanonicalOwnerId"/> as purge matches it. A memory in both is returned once, as the cache
    /// holds it, since the cache carries recalls and writes the next sync has not reached the backend with yet. Only
    /// memory nodes are read; the backend also holds document-derived graph content and other subsystems' nodes.
    /// </summary>
    /// <param name="subject">The person the copy is for.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One record per memory, ordered by id.</returns>
    public async Task<IReadOnlyList<JsonElement>> ExportSubjectAsync(ExportSubject subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);

        var cached = CachedMemoriesOwnedBy(subject.CanonicalOwnerId)
            .ToDictionary(r => r.Id, ToExported, StringComparer.Ordinal);
        foreach (var node in await BackendMemoriesOwnedByAsync(subject.CanonicalOwnerId, cancellationToken).ConfigureAwait(false))
            cached.TryAdd(node.Id, ToExported(node));

        return [.. cached.Values
            .OrderBy(m => m.Id, StringComparer.Ordinal)
            .Select(m => JsonSerializer.SerializeToElement(m, JsonSerializerOptions.Web))];
    }

    /// <summary>The cached memories <paramref name="canonicalOwner"/> owns. Owners are canonicalized on write. Shared with purge.</summary>
    private IEnumerable<MemoryRecord> CachedMemoriesOwnedBy(string canonicalOwner) =>
        _cache.Values.Where(r => ScopeIdentity.AreSame(r.OwnerId, canonicalOwner));

    /// <summary>The memory nodes <paramref name="canonicalOwner"/> owns in the durable backend. Shared with purge.</summary>
    private async Task<IReadOnlyList<GraphNode>> BackendMemoriesOwnedByAsync(
        string canonicalOwner, CancellationToken cancellationToken) =>
        [.. (await _graphBackend.GetNodesByOwnerAsync(canonicalOwner, cancellationToken).ConfigureAwait(false))
            .Where(n => string.Equals(n.Type, MemoryNodeType, StringComparison.Ordinal))];

    private static ExportedMemory ToExported(MemoryRecord record) => new(
        record.Id, record.Content, record.Source, record.Weight, record.CreatedAt, record.LastAccessedAt,
        record.AccessCount, record.Metadata);

    /// <summary>A backend node as the sync wrote it; a property it never wrote is left out rather than guessed.</summary>
    private static ExportedMemory ToExported(GraphNode node) => new(
        node.Id,
        node.Properties.GetValueOrDefault("content"),
        node.Properties.GetValueOrDefault("source"),
        double.TryParse(node.Properties.GetValueOrDefault("weight"), NumberStyles.Float, CultureInfo.InvariantCulture, out var weight) ? weight : null,
        ParseTime(node.Properties.GetValueOrDefault("created_at")),
        ParseTime(node.Properties.GetValueOrDefault("last_accessed_at")),
        int.TryParse(node.Properties.GetValueOrDefault("access_count"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : null,
        null);

    private static DateTimeOffset? ParseTime(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) ? time : null;

    /// <summary>One memory as it appears in a person's copy.</summary>
    private sealed record ExportedMemory(
        string Id, string? Content, string? Source, double? Weight, DateTimeOffset? CreatedAt, DateTimeOffset? LastAccessedAt,
        int? AccessCount, IReadOnlyDictionary<string, string>? Metadata);
}
