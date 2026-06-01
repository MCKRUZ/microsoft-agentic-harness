using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Domain.AI.Evaluation;

namespace Infrastructure.AI.Evaluation.Persistence;

/// <summary>
/// No-op <see cref="IEvalRunStore"/> registered as the default when
/// <see cref="Domain.Common.Config.AI.EvalDashboardOptions.PersistenceEnabled"/>
/// is <c>false</c>. Consumers can resolve <see cref="IEvalRunStore"/>
/// unconditionally without checking the option, matching the always-on shape
/// the prompt-registry recorder pipeline established for its OTel-only default.
/// </summary>
/// <remarks>
/// All writes are silently dropped; <see cref="AppendAsync"/> always returns
/// <c>false</c> (semantically: "I did not write a new row"). All reads return
/// empty / <c>null</c>. Consumers that want a hard guarantee about persistence
/// being on should inspect the option directly.
/// </remarks>
public sealed class NullEvalRunStore : IEvalRunStore
{
    /// <inheritdoc />
    public Task<bool> AppendAsync(
        EvalRunReport report,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        return Task.FromResult(false);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<EvalRunSummary>> GetRecentAsync(
        int take,
        CancellationToken cancellationToken)
    {
        if (take <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(take), take, "take must be positive.");
        }
        return Task.FromResult<IReadOnlyList<EvalRunSummary>>([]);
    }

    /// <inheritdoc />
    public Task<EvalRunReport?> GetRunDetailAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        return Task.FromResult<EvalRunReport?>(null);
    }
}
