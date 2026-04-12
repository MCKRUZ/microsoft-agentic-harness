using Domain.Common.MetaHarness;

namespace Application.AI.Common.Interfaces.MetaHarness;

/// <summary>
/// Persistence store for <see cref="HarnessCandidate"/> records.
/// All write operations must be atomic (temp-file + rename).
/// </summary>
public interface IHarnessCandidateRepository
{
    /// <summary>Persists a candidate and updates the run index.</summary>
    Task SaveAsync(HarnessCandidate candidate, CancellationToken ct = default);

    /// <summary>Returns the candidate with the given ID, or null if not found.</summary>
    Task<HarnessCandidate?> GetAsync(Guid candidateId, CancellationToken ct = default);

    /// <summary>
    /// Returns the full ancestor chain ending at <paramref name="candidateId"/>,
    /// ordered oldest-first (seed candidate at index 0).
    /// </summary>
    Task<IReadOnlyList<HarnessCandidate>> GetLineageAsync(Guid candidateId, CancellationToken ct = default);

    /// <summary>
    /// Returns the best evaluated candidate for the given run using tie-breaking:
    /// (1) highest pass rate, (2) lowest token cost, (3) lowest iteration.
    /// Reads only the index file to select the winner — does not open candidate.json
    /// files for non-winning candidates.
    /// </summary>
    Task<HarnessCandidate?> GetBestAsync(Guid optimizationRunId, CancellationToken ct = default);

    /// <summary>Returns all candidates for the given optimization run.</summary>
    Task<IReadOnlyList<HarnessCandidate>> ListAsync(Guid optimizationRunId, CancellationToken ct = default);
}
