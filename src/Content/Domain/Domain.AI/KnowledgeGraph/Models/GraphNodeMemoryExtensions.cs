namespace Domain.AI.KnowledgeGraph.Models;

/// <summary>
/// Extension helpers for reading and writing the memory trust marker carried by a
/// <see cref="GraphNode"/>. The marker rides in <see cref="GraphNode.Properties"/> — the portable,
/// string-valued metadata bag persisted identically by every graph backend (in-memory, Neo4j,
/// PostgreSQL) — so trust classification needs no schema change across backends.
/// </summary>
public static class GraphNodeMemoryExtensions
{
    /// <summary>
    /// The <see cref="GraphNode.Properties"/> key under which the <see cref="MemoryTrust"/> marker
    /// is stored, as the lowercase enum name (e.g. <c>"trusted"</c>, <c>"untrusted"</c>).
    /// </summary>
    public const string TrustPropertyKey = "memory.trust";

    /// <summary>
    /// Returns a copy of <paramref name="node"/> with its memory trust marker set to
    /// <paramref name="trust"/>. Existing properties are preserved.
    /// </summary>
    public static GraphNode WithTrust(this GraphNode node, MemoryTrust trust)
    {
        ArgumentNullException.ThrowIfNull(node);

        var properties = new Dictionary<string, string>(node.Properties)
        {
            [TrustPropertyKey] = trust.ToString().ToLowerInvariant()
        };

        return node with { Properties = properties };
    }

    /// <summary>
    /// Reads the memory trust marker from <paramref name="node"/>. Returns
    /// <see cref="MemoryTrust.Trusted"/> when no marker is present (legacy nodes, or nodes written
    /// while the memory guard was disabled), so unmarked facts remain recallable.
    /// </summary>
    public static MemoryTrust GetTrust(this GraphNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return node.Properties.TryGetValue(TrustPropertyKey, out var raw)
            && Enum.TryParse<MemoryTrust>(raw, ignoreCase: true, out var trust)
                ? trust
                : MemoryTrust.Trusted;
    }
}
