using Domain.AI.Compaction;
using Microsoft.Extensions.AI;

namespace Application.AI.Common.Interfaces.Compaction;

/// <summary>
/// Orchestrates context compaction using strategy selection, hook execution,
/// and prompt cache invalidation. Entry point for all compaction operations.
/// </summary>
public interface IContextCompactionService
{
    /// <summary>
    /// Compacts the message history using the specified strategy.
    /// Fires PreCompact/PostCompact hooks, invalidates prompt cache on success.
    /// </summary>
    /// <param name="agentId">The agent whose history is being compacted.</param>
    /// <param name="messages">The current message history to compact.</param>
    /// <param name="strategy">The compaction algorithm to apply.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The compaction result containing boundary marker and metrics.</returns>
    Task<CompactionResult> CompactAsync(
        string agentId,
        IReadOnlyList<ChatMessage> messages,
        CompactionStrategy strategy,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines whether auto-compaction should trigger based on current token usage.
    /// Returns false when the circuit breaker is tripped.
    /// </summary>
    /// <param name="agentId">The agent to check.</param>
    /// <param name="currentTokens">Current token consumption.</param>
    /// <param name="maxTokens">Maximum token budget for the agent.</param>
    /// <returns>True if auto-compaction should be triggered.</returns>
    bool ShouldAutoCompact(string agentId, int currentTokens, int maxTokens);
}
