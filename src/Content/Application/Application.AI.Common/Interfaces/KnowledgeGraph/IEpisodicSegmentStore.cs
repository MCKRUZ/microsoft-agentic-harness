using Domain.AI.KnowledgeGraph.Models;
using Domain.Common;

namespace Application.AI.Common.Interfaces.KnowledgeGraph;

/// <summary>
/// Persistence contract for <see cref="EpisodicSegment"/> records — the raw, untruncated conversation
/// grounding captured at each turn boundary for the harmonic memory representation (Memora port). The
/// write half is driven by <c>WorkEpisodeCaptureBehavior</c> (the same turn-boundary seam that emits
/// <see cref="Domain.AI.WorkMemory.WorkEpisode"/>); the read half is consumed by the harmonic recall path
/// (PR3).
/// </summary>
/// <remarks>
/// Reached only when <c>AppConfig:AI:HarmonicMemory:Mode</c> is not
/// <see cref="Domain.Common.Config.AI.HarmonicMemory.HarmonicMemoryMode.Off"/>. Tenant/owner isolation is
/// inherited from the injected <see cref="IKnowledgeGraphStore"/> (the tenant-isolating / compliance-aware
/// decorator chain), mirroring <c>GraphWorkEpisodeStore</c>.
/// </remarks>
public interface IEpisodicSegmentStore
{
    /// <summary>Persists an episodic segment. Segment IDs are unique; saving an existing ID overwrites it.</summary>
    /// <param name="segment">The raw episodic segment to store.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result> SaveAsync(EpisodicSegment segment, CancellationToken ct);

    /// <summary>
    /// Retrieves all episodic segments captured within the given conversation, most recent first.
    /// Returns a success result with an empty list when the conversation has no segments.
    /// </summary>
    /// <param name="conversationId">The conversation to retrieve segments for.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<IReadOnlyList<EpisodicSegment>>> GetByConversationAsync(string conversationId, CancellationToken ct);
}
