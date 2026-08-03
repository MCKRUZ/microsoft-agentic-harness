using Domain.AI.SkillTraining;

namespace Application.AI.Common.Interfaces.SkillTraining;

/// <summary>
/// Runs a candidate skill against a batch of evaluation items and returns per-item
/// <see cref="RolloutResult"/>s.
/// </summary>
/// <remarks>
/// <para>
/// Wraps whatever execution path the harness uses to invoke an agent with a specific skill
/// document at the system-prompt position. The Phase 5 wiring binds this to an
/// <c>IAgentInvoker</c>-based implementation that overrides the active skill with the
/// candidate content; for unit tests the orchestrator depends only on this interface
/// and tests inject deterministic stubs.
/// </para>
/// <para>
/// Implementations are expected to honor <c>batchSize</c> bounds at the call
/// site (e.g. via <c>SemaphoreSlim</c>) — the runner returns once all items in the
/// batch have been scored.
/// </para>
/// </remarks>
public interface IRolloutRunner
{
    /// <summary>
    /// Rolls out the candidate skill against the items identified in
    /// <paramref name="batch"/> and returns scored results.
    /// </summary>
    /// <param name="skillContent">The candidate skill document to use as system prompt.</param>
    /// <param name="batch">The rollout batch — split selection (train/val/test) plus item ids.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<RolloutResult>> RunAsync(
        string skillContent,
        RolloutBatch batch,
        CancellationToken cancellationToken);
}
