using Domain.AI.WorkMemory;
using Domain.Common;

namespace Application.AI.Common.Interfaces.WorkMemory;

/// <summary>
/// Persistence contract for <see cref="WorkEpisode"/> records — the agent's structured log of what
/// it did, turn by turn. The write half is driven by <c>WorkEpisodeCaptureBehavior</c>; the read half
/// is consumed by the overnight synthesis pass (PR2) and task-similarity recall (PR3).
/// </summary>
/// <remarks>
/// Keyed DI: <c>"graph"</c> (default, <c>GraphWorkEpisodeStore</c>) and <c>"in_memory"</c>
/// (<c>InMemoryWorkEpisodeStore</c>, for tests and ephemeral hosts). The provider is selected by
/// <c>WorkMemoryConfig.StoreProvider</c>.
/// </remarks>
public interface IWorkEpisodeStore
{
    /// <summary>Persists a work episode. Episode IDs are unique; saving an existing ID overwrites it.</summary>
    Task<Result> SaveAsync(WorkEpisode episode, CancellationToken ct);

    /// <summary>Retrieves an episode by ID. Returns a success result with a null value when not found.</summary>
    Task<Result<WorkEpisode?>> GetAsync(Guid episodeId, CancellationToken ct);

    /// <summary>Searches episodes matching the supplied <paramref name="criteria"/>.</summary>
    Task<Result<IReadOnlyList<WorkEpisode>>> SearchAsync(WorkEpisodeSearchCriteria criteria, CancellationToken ct);
}
