using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Config.AI.HarmonicMemory;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.KnowledgeGraph.Memory;

/// <summary>
/// The harmonic memory write path (Memora port) for <see cref="KnowledgeMemoryService"/>. Split into its
/// own partial so the legacy path stays the plainly-readable default and the enrichment logic — abstraction
/// and consolidation — lives together.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Consolidation is logical, not physical.</strong> Every remembered fact keeps its own
/// scope-namespaced, caller-keyed node — so isolation, trust classification, and deletion all stay strictly
/// per-fact. In <see cref="HarmonicMemoryMode.Full"/> a "merge" decision means the new fact <em>adopts the
/// canonical abstraction</em> of a similar existing entry (and unions its cue anchors), so related memories
/// share one topic and cluster together at recall time — without fusing storage, which would defeat the
/// write-gate's trust marker, break forget-by-key, and let repeated writes balloon content.
/// </para>
/// </remarks>
public sealed partial class KnowledgeMemoryService
{
    /// <summary>
    /// Whether the harmonic write path applies to this call: the mode must be raised above
    /// <see cref="HarmonicMemoryMode.Off"/> and the content must clear the configured length floor
    /// (short facts skip the LLM cost and take the legacy path).
    /// </summary>
    private static bool ShouldUseHarmonic(HarmonicMemoryConfig config, string content) =>
        config.Mode != HarmonicMemoryMode.Off
        && content.Length >= config.MinContentLengthChars;

    /// <summary>
    /// The harmonic write path. Produces a primary abstraction (+ cue anchors) for the fact and, in
    /// <see cref="HarmonicMemoryMode.Full"/>, may have the fact adopt a similar existing entry's canonical
    /// abstraction (consolidation). The fact is always persisted under its <em>own</em> scope-namespaced,
    /// caller-keyed node — consolidation chooses only which abstraction is stamped, never the content,
    /// identity, or trust — so the write-gate, isolation, and forget-by-key guarantees are all unchanged.
    /// </summary>
    private async Task RememberHarmonicAsync(
        string key,
        string content,
        string entityType,
        HarmonicMemoryConfig config,
        CancellationToken cancellationToken)
    {
        if (_abstractor is null)
        {
            // Real DI always registers the fail-fast NotConfiguredMemoryAbstractor default, so this is
            // only reachable from a unit test that raised the mode without wiring an abstractor. Fail
            // loud with the same guidance the NotConfigured default gives, rather than silently
            // downgrading to the legacy path (which would hide the misconfiguration).
            throw new InvalidOperationException(
                "Harmonic memory is enabled (AppConfig:AI:HarmonicMemory:Mode is not Off) but no " +
                "IMemoryAbstractor is available. Register an agent-backed implementation, or set Mode to Off.");
        }

        // 1. Gate FIRST — the single chokepoint, before any abstractor or consolidator LLM sees the
        //    content. Rejected content is never fed to a model, and the trust decision is identical to the
        //    legacy path (consolidation, below, never touches the fact's content, so gate order is safe).
        var decision = _writeGate is null
            ? null
            : await _writeGate.EvaluateAsync(key, content, entityType, cancellationToken);

        if (decision is { Persist: false })
        {
            _logger.LogWarning(
                "Harmonic memory write blocked for Key={Key}, Type={Type}: {Reason}",
                key, entityType, decision.Reason);
            return;
        }

        // 2. Quarantined facts are persisted for forensics but never recalled, so the harmonic scaffolding
        //    would never be read — and abstracting untrusted content would needlessly expose it to the
        //    abstractor LLM. Store them raw, exactly like the legacy path.
        if (decision is { Trust: MemoryTrust.Untrusted })
        {
            await PersistGatedNodeAsync(
                BuildMemoryNode(key, content, entityType, decision), decision, key, entityType, cancellationToken);
            return;
        }

        // 3. Trusted fact: abstract it (one LLM call). Output is untrusted; the abstractor is responsible
        //    for sanitizing it per the AI/LLM security rules.
        var candidateAbstraction = await _abstractor.AbstractAsync(content, cancellationToken);

        // 4. Consolidate (Full mode only): find a similar existing TRUSTED entry the consolidator wants this
        //    fact to join. A merge target only changes which abstraction we stamp — the fact keeps its own
        //    key, content, and trust.
        var mergeTarget = config.Mode == HarmonicMemoryMode.Full
            ? await ResolveMergeTargetAsync(candidateAbstraction, content, key, config.ConsolidationTopK, cancellationToken)
            : null;

        // On adoption, take the target's established canonical abstraction and union in the candidate's cue
        // anchors, so related memories share one organizing topic. On create, the candidate's own stands.
        var abstractionToStore = mergeTarget is null
            ? candidateAbstraction
            : MergeAbstraction(mergeTarget, candidateAbstraction);

        // 5. Build the node under the fact's OWN scope-namespaced key. Identity, trust, and deletion stay
        //    per-fact; consolidation only chose the abstraction stamped into Properties.
        var node = BuildMemoryNode(key, content, entityType, decision).WithAbstraction(abstractionToStore);

        await PersistGatedNodeAsync(node, decision, key, entityType, cancellationToken);
    }

    /// <summary>
    /// Finds similar existing memory entries by abstraction and asks the consolidator whether the new fact
    /// should adopt one's canonical abstraction. Returns the resolved target node (for its abstraction and
    /// cue anchors), or <see langword="null"/> to keep the candidate's own abstraction (no similar entries,
    /// no consolidator, an unknown target id, or an explicit create decision).
    /// </summary>
    private async Task<GraphNode?> ResolveMergeTargetAsync(
        MemoryAbstraction candidate,
        string candidateValue,
        string currentKey,
        int topK,
        CancellationToken cancellationToken)
    {
        var candidates = await FindConsolidationCandidatesAsync(candidate.Abstraction, currentKey, topK, cancellationToken);
        if (candidates.Count == 0)
            return null;

        // No consolidator wired (unit tests, or a consumer that only wants AbstractOnly-style behavior in
        // Full mode) => keep the candidate's own abstraction, never a blind adoption.
        if (_consolidator is null)
            return null;

        var existing = candidates
            .Select(n => new ExistingMemory
            {
                Id = n.Id,
                Abstraction = n.GetAbstraction() ?? n.Name,
                Value = n.Properties.GetValueOrDefault("content", string.Empty)
            })
            .ToList();

        var decision = await _consolidator.ConsolidateAsync(candidate, candidateValue, existing, cancellationToken);

        // Model output is untrusted: an unknown target id is treated as create-new, per the seam contract.
        return decision.Action == ConsolidationAction.Merge && decision.TargetId is not null
            ? candidates.FirstOrDefault(n => n.Id == decision.TargetId)
            : null;
    }

    /// <summary>
    /// Retrieves this scope's harmonic memory nodes whose abstraction overlaps the candidate's, ranked by
    /// token overlap, top-K. Reuses the scope-filtered <see cref="IKnowledgeGraphStore.GetAllNodesAsync"/>
    /// (the tenant-isolating decorator filters it to the caller's visible nodes) and additionally restricts
    /// to this scope's <c>memory:</c> nodes — excluding the node the current key itself would write to — so
    /// consolidation never considers corpus entities, another scope's memory, or the fact being written.
    /// </summary>
    /// <remarks>
    /// Ranking is deliberately lightweight (token-overlap, no embeddings) — semantic embedding-based recall
    /// is the PR3 read-path concern. The full-scan is O(n) over the visible graph; acceptable at template
    /// scale, and a follow-up would add an abstraction-indexed query primitive for large backends (the same
    /// caveat carried by <c>GetNodeCountAsync</c> and the retention scan).
    /// </remarks>
    private async Task<IReadOnlyList<GraphNode>> FindConsolidationCandidatesAsync(
        string candidateAbstraction,
        string currentKey,
        int topK,
        CancellationToken cancellationToken)
    {
        var all = await _graphStore.GetAllNodesAsync(cancellationToken);
        var scopePrefix = $"memory:{ScopeKey()}:";
        var selfId = MemoryNodeId(currentKey);
        var candidateTokens = Tokenize(candidateAbstraction);
        if (candidateTokens.Count == 0)
            return [];

        return all
            .Where(n => n.Id.StartsWith(scopePrefix, StringComparison.Ordinal) && n.Id != selfId)
            // Quarantined entries are never served to recall; likewise they are never offered to the
            // consolidator LLM, so untrusted content and its metadata cannot ride onto a trusted fact.
            // (Quarantined facts also carry no abstraction, but this mirrors IsRecallable as defense-in-depth.)
            .Where(n => n.GetTrust() == MemoryTrust.Trusted)
            .Select(n => (Node: n, Abstraction: n.GetAbstraction()))
            .Where(x => x.Abstraction is not null)
            .Select(x => (x.Node, Score: TokenOverlap(candidateTokens, Tokenize(x.Abstraction!))))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Select(x => x.Node)
            .ToList();
    }

    /// <summary>
    /// Builds the abstraction stamped when a fact adopts a similar entry: the target's canonical primary
    /// abstraction (falling back to the candidate's when the legacy target carries none), with the union of
    /// both entries' cue anchors (case-insensitive dedupe, target order first). Adoption accumulates
    /// retrieval hooks rather than discarding the incoming ones.
    /// </summary>
    private static MemoryAbstraction MergeAbstraction(GraphNode target, MemoryAbstraction candidate)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var anchors = new List<string>();
        foreach (var anchor in target.GetCueAnchors().Concat(candidate.CueAnchors))
        {
            if (!string.IsNullOrWhiteSpace(anchor) && seen.Add(anchor.Trim()))
                anchors.Add(anchor.Trim());
        }

        return new MemoryAbstraction
        {
            Abstraction = target.GetAbstraction() ?? candidate.Abstraction,
            CueAnchors = anchors
        };
    }

    /// <summary>Lowercases and splits text into a distinct set of alphanumeric word tokens for overlap scoring.</summary>
    private static HashSet<string> Tokenize(string text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in text.Split(
            [' ', '\t', '\n', '\r', '-', '_', '.', ',', ':', ';', '/', '\\', '(', ')', '[', ']', '{', '}', '"', '\''],
            StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.ToLowerInvariant();
            if (token.Length > 0)
                tokens.Add(token);
        }

        return tokens;
    }

    /// <summary>Jaccard similarity between two token sets: |intersection| / |union|, in [0, 1].</summary>
    private static double TokenOverlap(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
            return 0;

        var intersection = a.Count(b.Contains);
        var union = a.Count + b.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }
}
