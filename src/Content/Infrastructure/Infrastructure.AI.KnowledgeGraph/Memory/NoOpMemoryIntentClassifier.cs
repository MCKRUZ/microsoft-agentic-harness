using Application.AI.Common.Interfaces.KnowledgeGraph;

namespace Infrastructure.AI.KnowledgeGraph.Memory;

/// <summary>
/// Fail-open default <see cref="IMemoryIntentClassifier"/>: reports every candidate fact as aligned.
/// Registered so the memory write gate always resolves a classifier; consumers replace it with a
/// real (typically LLM-backed) implementation and set <c>MemoryGuardConfig.IntentCheckEnabled</c>
/// to activate the check. With this default in place the gate's protection is the deterministic
/// prompt-injection scan alone.
/// </summary>
public sealed class NoOpMemoryIntentClassifier : IMemoryIntentClassifier
{
    /// <inheritdoc />
    public Task<MemoryIntentResult> ClassifyAsync(
        string content,
        string entityType,
        CancellationToken cancellationToken = default)
        => Task.FromResult(MemoryIntentResult.AlignedResult);
}
