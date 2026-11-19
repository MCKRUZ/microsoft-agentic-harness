using System.Collections.Concurrent;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.WorkMemory;
using Domain.AI.KnowledgeGraph.Scoping;
using Domain.AI.WorkMemory;
using Domain.Common;
using Domain.Common.Compliance;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.AI.KnowledgeGraph.WorkMemory;

/// <summary>
/// In-memory implementation of <see cref="IWorkEpisodeStore"/> for tests and ephemeral hosts.
/// Registered with keyed DI key <c>"in_memory"</c>. Thread-safe; provides no tenant isolation —
/// that is a property of the graph-backed store only.
/// </summary>
/// <remarks>
/// Like the graph store, it attributes each saved episode to the caller in flight: the owner and tenant
/// are taken from the ambient request scope's <see cref="IKnowledgeScope"/> and overwrite whatever the
/// caller put on the episode, so work-memory synthesis can keep each user's lessons with that user (#968).
/// </remarks>
[PersonalDataStore(
    "Work-memory episodes held in process memory; lost at restart. Each episode keeps a turn's user message, the agent's response summary, its outcome and token counts, stamped with the owner and tenant of the caller who saved it. Owner erasure removes the person's episodes and the owner's export returns them; episodes saved with no caller in flight carry no owner and are reached by neither.",
    PersonalDataCategories.Identifiers | PersonalDataCategories.ConversationContent,
    PersonalDataKey.Owner,
    RetentionBasis.Volatile,
    ErasureSupport.PurgeByOwner,
    ExportSupport.ByOwner)]
public sealed partial class InMemoryWorkEpisodeStore : IWorkEpisodeStore
{
    private readonly ConcurrentDictionary<Guid, WorkEpisode> _episodes = new();
    private readonly IAmbientRequestScope _ambientScope;

    /// <summary>Initializes a new instance of the <see cref="InMemoryWorkEpisodeStore"/> class.</summary>
    /// <param name="ambientScope">The ambient request scope the caller's owner and tenant are read from on save.</param>
    public InMemoryWorkEpisodeStore(IAmbientRequestScope ambientScope)
    {
        ArgumentNullException.ThrowIfNull(ambientScope);
        _ambientScope = ambientScope;
    }

    /// <inheritdoc />
    public Task<Result> SaveAsync(WorkEpisode episode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(episode);
        var scope = _ambientScope.Current?.GetService<IKnowledgeScope>();
        _episodes[episode.EpisodeId] = episode with
        {
            OwnerId = ScopeIdentity.Canonicalize(scope?.UserId),
            TenantId = ScopeIdentity.Canonicalize(scope?.TenantId)
        };
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
