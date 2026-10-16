using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.SkillTraining;
using Domain.AI.SkillTraining;

namespace Application.AI.Common.Services.SkillTraining;

/// <summary>
/// Thread-safe in-memory <see cref="ISkillTrainingCheckpointStore"/>. Default registration
/// for development and tests; <c>EfSkillTrainingCheckpointStore</c> replaces it where checkpoints must survive a restart.
/// </summary>
/// <remarks>
/// <para>
/// Backed by a <see cref="ConcurrentDictionary{TKey, TValue}"/> keyed by RunId. State is lost
/// on process restart — appropriate for dev loops and unit tests, not for long-running
/// training that must survive failures.
/// </para>
/// <para>
/// To prevent unbounded growth on long-lived hosts, each run retains at most
/// <see cref="MaxCheckpointsPerRun"/> checkpoints — the best-scoring plus the most-recent.
/// </para>
/// </remarks>
public sealed class InMemorySkillTrainingCheckpointStore : ISkillTrainingCheckpointStore
{
    /// <summary>Per-run retention cap. Best-scoring checkpoint is always kept; oldest others evicted.</summary>
    public const int MaxCheckpointsPerRun = 64;

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, SkillTrainingCheckpoint>> _byRun = new();
    private readonly object _evictionLock = new();
    private readonly ConcurrentDictionary<string, byte> _activeRuns = new();

    /// <inheritdoc />
    public Task SaveAsync(SkillTrainingCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        cancellationToken.ThrowIfCancellationRequested();

        var byStep = _byRun.GetOrAdd(checkpoint.RunId, _ => new ConcurrentDictionary<int, SkillTrainingCheckpoint>());
        byStep[checkpoint.Step] = checkpoint;

        if (byStep.Count > MaxCheckpointsPerRun)
        {
            EvictOldest(byStep);
        }
        return Task.CompletedTask;
    }

    private void EvictOldest(ConcurrentDictionary<int, SkillTrainingCheckpoint> byStep)
    {
        // Lock per-run so concurrent saves don't double-evict.
        lock (_evictionLock)
        {
            if (byStep.Count <= MaxCheckpointsPerRun) return;

            // Best by score (ties broken by latest step) must survive; evict next-oldest among the rest.
            SkillTrainingCheckpoint? best = null;
            foreach (var cp in byStep.Values)
            {
                if (best is null || cp.Score > best.Score
                    || (cp.Score == best.Score && cp.Step > best.Step))
                {
                    best = cp;
                }
            }

            var oldestStep = int.MaxValue;
            foreach (var cp in byStep.Values)
            {
                if (cp.Step != best!.Step && cp.Step < oldestStep) oldestStep = cp.Step;
            }
            if (oldestStep != int.MaxValue)
            {
                byStep.TryRemove(oldestStep, out _);
            }
        }
    }

    /// <inheritdoc />
    public Task<SkillTrainingCheckpoint?> GetAsync(string runId, int step, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        cancellationToken.ThrowIfCancellationRequested();

        if (_byRun.TryGetValue(runId, out var byStep) && byStep.TryGetValue(step, out var cp))
        {
            return Task.FromResult<SkillTrainingCheckpoint?>(cp);
        }
        return Task.FromResult<SkillTrainingCheckpoint?>(null);
    }

    /// <inheritdoc />
    public Task<SkillTrainingCheckpoint?> GetBestAsync(string runId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_byRun.TryGetValue(runId, out var byStep) || byStep.IsEmpty)
        {
            return Task.FromResult<SkillTrainingCheckpoint?>(null);
        }

        SkillTrainingCheckpoint? best = null;
        foreach (var cp in byStep.Values)
        {
            if (best is null
                || cp.Score > best.Score
                || (cp.Score == best.Score && cp.Step > best.Step))
            {
                best = cp;
            }
        }
        return Task.FromResult(best);
    }

    /// <inheritdoc />
    /// <remarks>Within this store instance only: a claim here is not visible to another process.</remarks>
    public Task<IAsyncDisposable?> TryBeginRunAsync(string runId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult<IAsyncDisposable?>(
            _activeRuns.TryAdd(runId, 0) ? new Claim(_activeRuns, runId) : null);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SkillTrainingCheckpoint>> ListAsync(string runId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_byRun.TryGetValue(runId, out var byStep))
        {
            return Task.FromResult<IReadOnlyList<SkillTrainingCheckpoint>>([]);
        }

        var ordered = byStep.Values.OrderBy(c => c.Step).ToList();
        return Task.FromResult<IReadOnlyList<SkillTrainingCheckpoint>>(ordered);
    }

    private sealed class Claim(ConcurrentDictionary<string, byte> active, string runId) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            active.TryRemove(runId, out _);
            return ValueTask.CompletedTask;
        }
    }
}
