using Domain.AI.Compaction;
using Microsoft.Extensions.AI;

namespace Application.AI.Common.Interfaces.Compaction;

/// <summary>
/// Executes a specific compaction strategy. Implementations are registered
/// with keyed DI by <see cref="CompactionStrategy"/> enum value.
/// </summary>
public interface ICompactionStrategyExecutor
{
    /// <summary>Gets the strategy this executor implements.</summary>
    CompactionStrategy Strategy { get; }

    /// <summary>
    /// Executes the compaction strategy on the given messages.
    /// </summary>
    /// <param name="agentId">The agent whose history is being compacted.</param>
    /// <param name="messages">The message history to compact.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The compaction result with boundary marker and metrics.</returns>
    Task<CompactionResult> ExecuteAsync(
        string agentId,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default);
}
