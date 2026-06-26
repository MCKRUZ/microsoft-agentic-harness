using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.WorkMemory;
using Domain.AI.WorkMemory;
using Domain.Common;

namespace Infrastructure.AI.KnowledgeGraph.WorkMemory;

/// <summary>
/// In-memory implementation of <see cref="IWorkEpisodeStore"/> for tests and ephemeral hosts.
/// Registered with keyed DI key <c>"in_memory"</c>. Thread-safe; provides no tenant isolation —
/// that is a property of the graph-backed store only.
/// </summary>
public sealed class InMemoryWorkEpisodeStore : IWorkEpisodeStore
{
    private readonly ConcurrentDictionary<Guid, WorkEpisode> _episodes = new();

    /// <inheritdoc />
    public Task<Result> SaveAsync(WorkEpisode episode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(episode);
        _episodes[episode.EpisodeId] = episode;
        return Task.FromResult(Result.Success());
    }

    /// <inheritdoc />
    public Task<Result<WorkEpisode?>> GetAsync(Guid episodeId, CancellationToken ct)
    {
        _episodes.TryGetValue(episodeId, out var episode);
        return Task.FromResult(Result<WorkEpisode?>.Success(episode));
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<WorkEpisode>>> SearchAsync(WorkEpisodeSearchCriteria criteria, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        IEnumerable<WorkEpisode> query = _episodes.Values;

        if (criteria.ConversationId is not null)
            query = query.Where(e => e.ConversationId == criteria.ConversationId);
        if (criteria.Outcome is not null)
            query = query.Where(e => e.Outcome == criteria.Outcome);
        if (criteria.CreatedAfter is not null)
            query = query.Where(e => e.CreatedAt >= criteria.CreatedAfter);
        if (criteria.CreatedBefore is not null)
            query = query.Where(e => e.CreatedAt <= criteria.CreatedBefore);

        // Newest first — synthesis cares about recent work; deterministic for tests.
        query = query.OrderByDescending(e => e.CreatedAt);

        if (criteria.Limit is { } limit)
            query = query.Take(limit);

        return Task.FromResult(Result<IReadOnlyList<WorkEpisode>>.Success(query.ToList()));
    }
}
