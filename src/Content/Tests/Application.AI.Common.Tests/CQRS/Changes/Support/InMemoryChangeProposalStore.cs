using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;

namespace Application.AI.Common.Tests.CQRS.Changes.Support;

/// <summary>
/// In-memory <see cref="IChangeProposalStore"/> implementation used by handler tests
/// to exercise the full CQRS path without standing up persistence. Thread-safe;
/// stores by id with last-write-wins semantics matching the production store contract.
/// </summary>
internal sealed class InMemoryChangeProposalStore : IChangeProposalStore
{
    private readonly ConcurrentDictionary<string, ChangeProposal> _byId = new(StringComparer.Ordinal);

    public Task<ChangeProposal?> GetAsync(string id, CancellationToken cancellationToken)
    {
        _byId.TryGetValue(id, out var proposal);
        return Task.FromResult(proposal);
    }

    public Task SaveAsync(ChangeProposal proposal, CancellationToken cancellationToken)
    {
        _byId[proposal.Id] = proposal;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChangeProposal>> ListAsync(
        ChangeProposalQuery query,
        CancellationToken cancellationToken)
    {
        IEnumerable<ChangeProposal> q = _byId.Values;
        if (query.Status.HasValue)
            q = q.Where(p => p.Status == query.Status.Value);
        if (!string.IsNullOrEmpty(query.SubmittedByAgentId))
            q = q.Where(p => p.SubmittedBy.Id == query.SubmittedByAgentId);
        if (query.MinimumBlastRadius.HasValue)
            q = q.Where(p => p.BlastRadius >= query.MinimumBlastRadius.Value);
        if (query.TargetKind.HasValue)
            q = q.Where(p => p.Target.Kind == query.TargetKind.Value);

        IReadOnlyList<ChangeProposal> results = q
            .OrderByDescending(p => p.SubmittedAt)
            .Take(query.MaxResults)
            .ToList();
        return Task.FromResult(results);
    }
}
