using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;

namespace Infrastructure.AI.Changes;

/// <summary>
/// Production-default in-memory <see cref="IChangeProposalStore"/>. Suitable for
/// single-process hosts that don't need durability across restarts. Consumers
/// requiring durability swap this for an EF-Core-backed store registered in
/// place of it under the same DI lifetime.
/// </summary>
/// <remarks>
/// Thread-safe via <see cref="ConcurrentDictionary{TKey, TValue}"/>. Idempotent
/// on duplicate saves (last-write-wins on the same id). Registered as singleton
/// because state must survive across MediatR scopes within a host process.
/// </remarks>
public sealed class InMemoryChangeProposalStore : IChangeProposalStore
{
    private readonly ConcurrentDictionary<string, ChangeProposal> _byId =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<ChangeProposal?> GetAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        _byId.TryGetValue(id, out var proposal);
        return Task.FromResult(proposal);
    }

    /// <inheritdoc />
    public Task SaveAsync(ChangeProposal proposal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        _byId[proposal.Id] = proposal;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ChangeProposal>> ListAsync(
        ChangeProposalQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        IEnumerable<ChangeProposal> q = _byId.Values;
        if (query.Status.HasValue)
        {
            q = q.Where(p => p.Status == query.Status.Value);
        }
        if (!string.IsNullOrEmpty(query.SubmittedByAgentId))
        {
            q = q.Where(p => string.Equals(p.SubmittedBy.Id, query.SubmittedByAgentId, StringComparison.Ordinal));
        }
        if (query.MinimumBlastRadius.HasValue)
        {
            q = q.Where(p => p.BlastRadius >= query.MinimumBlastRadius.Value);
        }
        if (query.TargetKind.HasValue)
        {
            q = q.Where(p => p.Target.Kind == query.TargetKind.Value);
        }

        IReadOnlyList<ChangeProposal> results = q
            .OrderByDescending(p => p.SubmittedAt)
            .Take(Math.Max(0, query.MaxResults))
            .ToList();
        return Task.FromResult(results);
    }
}
