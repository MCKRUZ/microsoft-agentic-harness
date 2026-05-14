using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.Learnings;
using Domain.AI.Learnings;
using Domain.Common;

namespace Infrastructure.AI.KnowledgeGraph.Learnings;

/// <summary>
/// Simple in-memory implementation of <see cref="ILearningsStore"/> for testing.
/// Registered with keyed DI key <c>"in_memory"</c>.
/// </summary>
public sealed class InMemoryLearningsStore : ILearningsStore
{
    private readonly ConcurrentDictionary<Guid, LearningEntry> _entries = new();

    public Task<Result> SaveAsync(LearningEntry learning, CancellationToken ct)
    {
        _entries[learning.LearningId] = learning;
        return Task.FromResult(Result.Success());
    }

    public Task<Result<LearningEntry?>> GetAsync(Guid learningId, CancellationToken ct)
    {
        if (_entries.TryGetValue(learningId, out var entry) && !entry.IsDeleted)
            return Task.FromResult(Result<LearningEntry?>.Success(entry));

        return Task.FromResult(Result<LearningEntry?>.Success(null));
    }

    public Task<Result<IReadOnlyList<LearningEntry>>> SearchAsync(LearningSearchCriteria criteria, CancellationToken ct)
    {
        var results = _entries.Values
            .Where(e => !e.IsDeleted)
            .Where(e => MatchesScope(e.Scope, criteria.Scope))
            .Where(e => criteria.Category is null || e.Category == criteria.Category)
            .Where(e => criteria.MinFeedbackWeight is null || e.FeedbackWeight >= criteria.MinFeedbackWeight)
            .Where(e => criteria.CreatedAfter is null || e.CreatedAt >= criteria.CreatedAfter)
            .Where(e => criteria.CreatedBefore is null || e.CreatedAt <= criteria.CreatedBefore)
            .ToList();

        return Task.FromResult(Result<IReadOnlyList<LearningEntry>>.Success(results));
    }

    public Task<Result> UpdateAsync(LearningEntry learning, CancellationToken ct)
    {
        if (!_entries.ContainsKey(learning.LearningId))
            return Task.FromResult(Result.Fail("Learning not found"));

        _entries[learning.LearningId] = learning;
        return Task.FromResult(Result.Success());
    }

    public Task<Result> SoftDeleteAsync(Guid learningId, string reason, CancellationToken ct)
    {
        if (!_entries.TryGetValue(learningId, out var entry))
            return Task.FromResult(Result.Fail("Learning not found"));

        _entries[learningId] = entry with { IsDeleted = true, DeleteReason = reason };
        return Task.FromResult(Result.Success());
    }

    private static bool MatchesScope(LearningScope entryScope, LearningScope? criteriaScope)
    {
        if (criteriaScope is null)
            return true;

        if (entryScope.IsGlobal)
            return true;

        if (criteriaScope.AgentId is not null && entryScope.AgentId == criteriaScope.AgentId)
            return true;

        if (criteriaScope.TeamId is not null && entryScope.TeamId == criteriaScope.TeamId)
            return true;

        return false;
    }
}
