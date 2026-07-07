using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.Retrieval;
using Domain.Common.Config.AI.HarmonicMemory;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.KnowledgeGraph.Memory;

/// <summary>
/// The harmonic memory recall path (Memora port) for <see cref="KnowledgeMemoryService"/>. Split into its
/// own partial so the legacy substring/graph recall stays the plainly-readable default and the harmonic
/// read logic — matching the query against the primary abstraction + cue anchors, expanding the shared-anchor
/// cluster, and fusing with the legacy list — lives together.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes the write-side scaffolding pay off: the write path stamps a primary abstraction and cue
/// anchors onto each trusted node, but the legacy recall (<c>SearchGraphAsync</c>) matches only the node's
/// name/type and never reads them. Harmonic recall matches the query against those indexed fields — a second
/// set of entry points the substring path lacks — then fuses the two rankings so nothing the legacy path
/// found is lost.
/// </para>
/// <para>
/// <strong>Matching is lexical, no LLM on the recall hot path.</strong> The query is tokenized and scored by
/// token overlap against each candidate's abstraction and cue anchors (the same <c>Tokenize</c>/
/// <c>TokenOverlap</c> the write path uses). Recall runs per turn during context assembly, and a benchmark on
/// this codebase found embedding-based semantic matching adds negligible recall over lexical overlap for this
/// workload — so paying an LLM call (or standing up an embedder) per recall is not worth it here.
/// </para>
/// </remarks>
public sealed partial class KnowledgeMemoryService
{
    /// <summary>
    /// Harmonic recall: build the legacy list and the harmonic ranked list (abstraction + cue-anchor matches,
    /// plus the shared-cue-anchor cluster around them), then fuse the two with Reciprocal Rank Fusion. The
    /// final list is re-filtered through <see cref="IsRecallable"/> so the "quarantined facts are never
    /// served" invariant holds even though both inputs already filter — recall stays the single chokepoint.
    /// </summary>
    /// <remarks>
    /// The legacy list is fused first, so on a score tie a legacy hit outranks a harmonic-only hit: a fact the
    /// legacy (Off-mode) path would have surfaced at the top is never displaced by a merely-tying harmonic
    /// match. Harmonic matches still win when they score strictly higher (including facts found by both paths,
    /// whose contributions sum), which is the reranking harmonic recall exists to provide.
    /// </remarks>
    private async Task<IReadOnlyList<GraphNode>> RecallHarmonicFusedAsync(
        string query,
        int maxResults,
        HarmonicMemoryConfig config,
        CancellationToken cancellationToken)
    {
        // Cast a wider net than maxResults on each source so the fusion has enough overlap to reorder
        // meaningfully (mirrors the RAG hybrid retriever requesting extra candidates before fusing).
        var candidateCount = maxResults * 2;

        var legacyHits = await RecallLegacyAsync(query, candidateCount, cancellationToken);
        var harmonicHits = await RecallHarmonicAsync(query, candidateCount, config, cancellationToken);

        // Legacy first: on a tie its first-appearance order wins, preserving the legacy path's top hits.
        var fused = ReciprocalRankFusion.Fuse(
            [legacyHits, harmonicHits],
            static n => n.Id,
            config.RecallRrfK,
            topK: null);

        var results = fused
            .Select(f => f.Item)
            .Where(IsRecallable)
            .Take(maxResults)
            .ToList();

        _logger.LogDebug(
            "Harmonic recall: {HarmonicHits} harmonic, {LegacyHits} legacy, {Returned} returned (top {Max})",
            harmonicHits.Count, legacyHits.Count, results.Count, maxResults);

        return results;
    }

    /// <summary>
    /// Ranks this scope's trusted harmonic nodes against the query by the better of their abstraction- or
    /// cue-anchor-token overlap, then expands the top matches with their shared-cue-anchor neighbors (the
    /// implicit memory graph) so a query that hits one member of a cluster surfaces the coherent cluster.
    /// Direct matches rank ahead of the traversal neighbors they pulled in.
    /// </summary>
    private async Task<IReadOnlyList<GraphNode>> RecallHarmonicAsync(
        string query,
        int maxResults,
        HarmonicMemoryConfig config,
        CancellationToken cancellationToken)
    {
        var queryTokens = Tokenize(query);
        if (queryTokens.Count == 0)
            return [];

        var nodes = await GetScopedTrustedMemoryNodesAsync(cancellationToken);

        // Order by score, then by id — the id tiebreak keeps recall deterministic regardless of the graph
        // backend's node enumeration order (GetAllNodesAsync is not order-guaranteed), so the same query
        // returns the same facts run-to-run.
        var seeds = nodes
            .Select(n => (Node: n, Score: HarmonicMatchScore(queryTokens, n)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Node.Id, StringComparer.Ordinal)
            .Take(maxResults)
            .Select(x => x.Node)
            .ToList();

        if (seeds.Count == 0)
            return seeds;

        var neighbors = ExpandSharedCueAnchors(seeds, nodes, config.RecallCueAnchorFanout);

        // Seeds first (higher rank in the fused ranking), then the cluster neighbors they anchored.
        return seeds.Concat(neighbors).ToList();
    }

    /// <summary>
    /// Scores a node against the query as the maximum of its abstraction-token overlap and its
    /// cue-anchor-token overlap — a hit on either indexed field surfaces the node, matching Memora's joint
    /// abstraction+cue match. Zero when the node carries neither (which excludes it from recall).
    /// </summary>
    private static double HarmonicMatchScore(HashSet<string> queryTokens, GraphNode node)
    {
        var abstraction = node.GetAbstraction();
        var abstractionScore = abstraction is null
            ? 0.0
            : TokenOverlap(queryTokens, Tokenize(abstraction));

        var cueAnchors = node.GetCueAnchors();
        var cueScore = cueAnchors.Count == 0
            ? 0.0
            : TokenOverlap(queryTokens, Tokenize(string.Join(' ', cueAnchors)));

        return Math.Max(abstractionScore, cueScore);
    }

    /// <summary>
    /// Expands the seed set with other pool nodes that share at least one cue anchor with a seed — the
    /// implicit memory graph, where a shared <c>[Entity]+[Aspect]</c> anchor is an edge. Bounded by
    /// <paramref name="fanout"/> to keep the returned cluster focused; anchors are matched case-insensitively
    /// as whole phrases (matching how the write path dedupes them). Seeds themselves are never re-included.
    /// </summary>
    private static IReadOnlyList<GraphNode> ExpandSharedCueAnchors(
        IReadOnlyList<GraphNode> seeds,
        IReadOnlyList<GraphNode> pool,
        int fanout)
    {
        if (fanout <= 0)
            return [];

        var seedIds = seeds.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var seedAnchors = seeds
            .SelectMany(s => s.GetCueAnchors())
            .Select(NormalizeAnchor)
            .Where(a => a.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        if (seedAnchors.Count == 0)
            return [];

        return pool
            .Where(n => !seedIds.Contains(n.Id))
            .Where(n => n.GetCueAnchors().Any(a => seedAnchors.Contains(NormalizeAnchor(a))))
            // Deterministic id ordering before the fan-out cap, so which neighbors survive does not depend on
            // the graph backend's node enumeration order.
            .OrderBy(n => n.Id, StringComparer.Ordinal)
            .Take(fanout)
            .ToList();
    }

    /// <summary>Normalizes a cue anchor for case-insensitive whole-phrase comparison (trim + lowercase).</summary>
    private static string NormalizeAnchor(string anchor) => anchor.Trim().ToLowerInvariant();
}
