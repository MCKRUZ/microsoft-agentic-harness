using Domain.AI.SkillTraining;

namespace Application.AI.Common.Interfaces.SkillTraining;

/// <summary>
/// Persists <see cref="SkillTrainingCheckpoint"/>s for a training run so the orchestrator can
/// resume after a crash, audit the trajectory of accepted skills, and roll back to a prior step.
/// </summary>
/// <remarks>
/// Mirrors the plan-state-store pattern: short-lived store calls, all operations addressable
/// by (RunId, Step). Implementations may be in-memory (default for tests / dev) or backed
/// by a durable store (<c>EfSkillTrainingCheckpointStore</c>, SQLite).
/// </remarks>
public interface ISkillTrainingCheckpointStore
{
    /// <summary>Persists a checkpoint. Replaces any existing checkpoint at the same (RunId, Step).</summary>
    Task SaveAsync(SkillTrainingCheckpoint checkpoint, CancellationToken cancellationToken);

    /// <summary>Loads a specific checkpoint by (RunId, Step), or null when none exists.</summary>
    Task<SkillTrainingCheckpoint?> GetAsync(string runId, int step, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the highest-scoring checkpoint for the run, or null when the run has none yet.
    /// Ties broken by latest <see cref="SkillTrainingCheckpoint.Step"/>.
    /// </summary>
    Task<SkillTrainingCheckpoint?> GetBestAsync(string runId, CancellationToken cancellationToken);

    /// <summary>Returns every checkpoint for the run, ordered by step ascending.</summary>
    Task<IReadOnlyList<SkillTrainingCheckpoint>> ListAsync(string runId, CancellationToken cancellationToken);

    /// <summary>
    /// Claims the right to work on <paramref name="runId"/>, so two callers cannot train the same run at once
    /// and write to the same checkpoints. Returns <see langword="null"/> when someone else holds it.
    /// </summary>
    /// <remarks>
    /// Dispose the result to release the claim. How far the claim reaches is the store's: an in-memory store
    /// claims within its process; a durable store claims across every process that shares it, and lets a
    /// claim lapse if its holder stops (a crash), so a dead process does not block the run for good.
    /// </remarks>
    /// <param name="runId">The run to claim.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IAsyncDisposable?> TryBeginRunAsync(string runId, CancellationToken cancellationToken);
}
