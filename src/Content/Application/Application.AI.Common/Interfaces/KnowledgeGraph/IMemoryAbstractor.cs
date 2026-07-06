using Domain.AI.KnowledgeGraph.Models;

namespace Application.AI.Common.Interfaces.KnowledgeGraph;

/// <summary>
/// Produces the indexed scaffolding layer — a primary abstraction and cue anchors — over a
/// cross-session memory value, for the harmonic memory representation (Memora port). The memory value
/// itself is never indexed; this abstraction and its cue anchors are.
/// </summary>
/// <remarks>
/// <para>
/// Implementations are expected to make a single LLM call per invocation and are therefore only reached
/// when <c>AppConfig:AI:HarmonicMemory:Mode</c> is not <see cref="Domain.Common.Config.AI.HarmonicMemory.HarmonicMemoryMode.Off"/>.
/// The harness ships <see cref="Application.AI.Common.Services.KnowledgeGraph.NotConfiguredMemoryAbstractor"/>
/// as a fail-fast default; template consumers replace it with an agent-backed implementation in
/// Infrastructure.AI.
/// </para>
/// <para>
/// Model output is untrusted: implementations must wrap the memory content in XML tags to defend against
/// prompt injection and sanitize the returned abstraction and cue anchors, consistent with the harness
/// AI/LLM security rules.
/// </para>
/// </remarks>
public interface IMemoryAbstractor
{
    /// <summary>
    /// Generates a primary abstraction and cue anchors for the given memory content.
    /// </summary>
    /// <param name="content">The full memory value to abstract over.</param>
    /// <param name="cancellationToken">Cancellation token with the abstraction timeout.</param>
    /// <returns>The primary abstraction and its cue anchors.</returns>
    Task<MemoryAbstraction> AbstractAsync(
        string content,
        CancellationToken cancellationToken = default);
}
