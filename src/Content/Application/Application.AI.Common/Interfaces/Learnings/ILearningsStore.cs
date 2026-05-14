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
}
