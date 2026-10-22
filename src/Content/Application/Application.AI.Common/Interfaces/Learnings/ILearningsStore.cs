using Domain.AI.Learnings;
using Domain.Common;

namespace Application.AI.Common.Interfaces.Learnings;

/// <summary>
/// Persistence contract for learning entries.
/// Keyed DI: <c>"graph"</c> (default), <c>"in_memory"</c> (testing).
/// </summary>
public interface ILearningsStore
{
    /// <summary>Persists a new learning entry.</summary>
    Task<Result> SaveAsync(LearningEntry learning, CancellationToken ct);

    /// <summary>Retrieves a learning by ID. Returns null value when not found.</summary>
    Task<Result<LearningEntry?>> GetAsync(Guid learningId, CancellationToken ct);

    /// <summary>Searches learnings matching the specified criteria with scope-aware filtering.</summary>
    Task<Result<IReadOnlyList<LearningEntry>>> SearchAsync(LearningSearchCriteria criteria, CancellationToken ct);

    /// <summary>Updates an existing learning entry (feedback weight, access time, etc.).</summary>
    Task<Result> UpdateAsync(LearningEntry learning, CancellationToken ct);

    /// <summary>Marks a learning as soft-deleted with a reason. Excluded from future searches.</summary>
    Task<Result> SoftDeleteAsync(Guid learningId, string reason, CancellationToken ct);

    /// <summary>
    /// Permanently removes every learning soft-deleted before <paramref name="deletedBefore"/> (#901). A soft-deleted
    /// learning keeps its content, so without this a learning someone asked to forget, or one that decayed, was kept for
    /// good. One soft-deleted before deletion times were recorded counts as deleted long ago.
    /// </summary>
    /// <param name="deletedBefore">Learnings soft-deleted before this instant are removed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many learnings were removed.</returns>
    Task<Result<int>> PurgeDeletedAsync(DateTimeOffset deletedBefore, CancellationToken ct);
}
