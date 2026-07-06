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

    /// <summary>
    /// The <see cref="GraphNode.Properties"/> key under which the harmonic memory primary abstraction
    /// (the "what is this memory about" canonical summary) is stored.
    /// </summary>
    public const string AbstractionPropertyKey = "memory.abstraction";

    /// <summary>
    /// The <see cref="GraphNode.Properties"/> key under which the harmonic memory cue anchors are stored,
    /// newline-joined. Cue anchors are short <c>[Entity] + [Aspect]</c> phrases; the abstractor sanitizes
    /// them to single-line values, so a newline join round-trips losslessly and stays portable across every
    /// graph backend without a JSON dependency in the domain layer.
    /// </summary>
    public const string CueAnchorsPropertyKey = "memory.cue_anchors";

    /// <summary>
    /// Returns a copy of <paramref name="node"/> carrying the harmonic scaffolding layer —
    /// <see cref="MemoryAbstraction.Abstraction"/> and its <see cref="MemoryAbstraction.CueAnchors"/> —
    /// in <see cref="GraphNode.Properties"/>. Existing properties (including the trust marker) are
    /// preserved. The cue-anchor key is written only when at least one anchor is present, mirroring how
    /// the trust marker is written only when it carries signal.
    /// </summary>
    /// <param name="node">The node to stamp.</param>
    /// <param name="abstraction">The primary abstraction and cue anchors to store.</param>
    public static GraphNode WithAbstraction(this GraphNode node, MemoryAbstraction abstraction)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(abstraction);

        var properties = new Dictionary<string, string>(node.Properties)
        {
            [AbstractionPropertyKey] = abstraction.Abstraction
        };

        var anchors = abstraction.CueAnchors
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .ToList();
        if (anchors.Count > 0)
            properties[CueAnchorsPropertyKey] = string.Join('\n', anchors);

        return node with { Properties = properties };
    }

    /// <summary>
    /// Reads the harmonic primary abstraction from <paramref name="node"/>, or <see langword="null"/>
    /// when the node carries none (legacy memory nodes, or nodes written with harmonic memory off).
    /// </summary>
    public static string? GetAbstraction(this GraphNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return node.Properties.TryGetValue(AbstractionPropertyKey, out var abstraction)
            && !string.IsNullOrWhiteSpace(abstraction)
                ? abstraction
                : null;
    }

    /// <summary>
    /// Reads the harmonic cue anchors from <paramref name="node"/>. Returns an empty list when the node
    /// carries none.
    /// </summary>
    public static IReadOnlyList<string> GetCueAnchors(this GraphNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return node.Properties.TryGetValue(CueAnchorsPropertyKey, out var raw)
            && !string.IsNullOrWhiteSpace(raw)
                ? raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [];
    }
}
