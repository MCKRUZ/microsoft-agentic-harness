using Domain.AI.Learnings;
using Domain.Common;

namespace Application.AI.Common.Interfaces.Learnings;

/// <summary>
/// Calculates temporal freshness and prunes expired learnings based on <see cref="DecayClass"/> rules.
/// </summary>
public interface ILearningDecayService
{
    /// <summary>Calculates the freshness score (0.0-1.0) for a learning based on its decay class and age.</summary>
    Task<double> CalculateFreshnessAsync(LearningEntry learning, CancellationToken ct);

    /// <summary>Soft-deletes learnings whose freshness has dropped below the configured threshold. Returns the count pruned.</summary>
    Task<Result<int>> PruneExpiredAsync(CancellationToken ct);
}
