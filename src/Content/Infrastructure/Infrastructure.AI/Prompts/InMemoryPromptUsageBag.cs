using System.Collections.Concurrent;
using Application.AI.Common.Prompts.Interfaces;
using Domain.AI.Prompts;

namespace Infrastructure.AI.Prompts;

/// <summary>
/// In-memory thread-safe <see cref="IPromptUsageBag"/> backed by a
/// <see cref="ConcurrentQueue{T}"/>. Designed for DI Scoped lifetime so each MediatR
/// request gets a fresh accumulator; no persistence, no cross-request state.
/// </summary>
/// <remarks>
/// <see cref="Drain"/> uses <c>TryDequeue</c> in a loop to preserve insertion order and
/// remove every entry atomically per-call. Entries added concurrently with <see cref="Drain"/>
/// either land in this drain or the next one — never both, never neither.
/// </remarks>
public sealed class InMemoryPromptUsageBag : IPromptUsageBag
{
    private readonly ConcurrentQueue<PromptUsageBagEntry> _entries = new();

    /// <inheritdoc />
    public void Track(PromptDescriptor descriptor, PromptUsageContext context)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(context);
        _entries.Enqueue(new PromptUsageBagEntry(descriptor, context));
    }

    /// <inheritdoc />
    public IReadOnlyList<PromptUsageBagEntry> Drain()
    {
        if (_entries.IsEmpty) return [];

        var drained = new List<PromptUsageBagEntry>(_entries.Count);
        while (_entries.TryDequeue(out var entry))
        {
            drained.Add(entry);
        }
        return drained;
    }
}
