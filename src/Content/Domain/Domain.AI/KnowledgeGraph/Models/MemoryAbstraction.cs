namespace Domain.AI.KnowledgeGraph.Models;

/// <summary>
/// The indexed scaffolding layer over a cross-session memory value, produced by an
/// <c>IMemoryAbstractor</c>. In the harmonic memory representation the memory <em>value</em> is not
/// indexed; this abstraction and its cue anchors are, giving precise recall without embedding fuzziness.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="Abstraction"/> is a one-to-one canonical summary of what the memory is about — the
/// stable organizing unit that consolidation and updates key on (e.g. "Project Orion Timeline").
/// </para>
/// <para>
/// <see cref="CueAnchors"/> are lightweight <c>[Entity] + [Aspect]</c> phrases (e.g. "Alice research
/// paper") that expose additional retrieval paths and form a many-to-many, implicit graph across
/// related memories through shared anchors.
/// </para>
/// </remarks>
public sealed record MemoryAbstraction
{
    /// <summary>
    /// The primary abstraction: a concise, canonical summary of what the memory is fundamentally about.
    /// Serves as the consolidation/update key.
    /// </summary>
    public required string Abstraction { get; init; }

    /// <summary>
    /// Cue anchors — short <c>[Entity] + [Aspect]</c> phrases (typically 1–3) that act as fine-grained
    /// semantic entry points into the memory. Empty when none were produced.
    /// </summary>
    public IReadOnlyList<string> CueAnchors { get; init; } = [];
}
