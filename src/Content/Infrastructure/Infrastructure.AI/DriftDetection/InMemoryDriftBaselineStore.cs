using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.DriftDetection;
using Domain.AI.DriftDetection;
using Domain.Common;

namespace Infrastructure.AI.DriftDetection;

/// <summary>
/// In-memory baseline store backed by <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// for thread-safe operation. Intended for testing and development scenarios where
/// graph persistence is unnecessary.
/// </summary>
/// <remarks>
/// Uses a composite key of <c>(<see cref="DriftScope"/>, string scopeIdentifier)</c>
/// for O(1) lookups. All operations return <see cref="Result.Success()"/> — this store
/// cannot fail under normal conditions. Data is not persisted across process restarts.
/// </remarks>
public sealed class InMemoryDriftBaselineStore : IDriftBaselineStore
{
    private readonly ConcurrentDictionary<(DriftScope Scope, string Identifier), DriftBaseline> _baselines = new();

    /// <inheritdoc />
    public Task<Result> SaveBaselineAsync(DriftBaseline baseline, CancellationToken ct)
    {
        var key = NormalizeKey(baseline.Scope, baseline.ScopeIdentifier);
        _baselines[key] = baseline;
        return Task.FromResult(Result.Success());
    }

    /// <inheritdoc />
    public Task<Result<DriftBaseline?>> GetBaselineAsync(
        DriftScope scope, string scopeIdentifier, CancellationToken ct)
    {
        var key = NormalizeKey(scope, scopeIdentifier);
        _baselines.TryGetValue(key, out var baseline);
        return Task.FromResult(Result<DriftBaseline?>.Success(baseline));
    }

    private static (DriftScope, string) NormalizeKey(DriftScope scope, string identifier) =>
        (scope, identifier.ToLowerInvariant());

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<DriftBaseline>>> GetBaselinesAsync(
        DriftScope? scope, CancellationToken ct)
    {
        var results = scope is null
            ? _baselines.Values.ToList()
            : _baselines.Values.Where(b => b.Scope == scope.Value).ToList();

        return Task.FromResult(Result<IReadOnlyList<DriftBaseline>>.Success(results.AsReadOnly()));
    }
}
