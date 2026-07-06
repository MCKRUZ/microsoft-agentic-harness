using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;

namespace Presentation.EvalRunner.HarmonicWriteEval;

/// <summary>
/// Offline, LLM-free <see cref="IMemoryAbstractor"/> for the harmonic write-eval. Derives an abstraction
/// from the fact's most significant keywords so the eval can run for free in CI and validate the harness
/// plumbing (fragmentation counting, cost accounting, report shape) without paid model calls.
/// </summary>
/// <remarks>
/// This is a plumbing proxy, not a quality reference — the <em>real</em> abstraction quality and clustering
/// numbers come from the LLM-backed provider on a paid run. It is eval-only and never shipped to consumers.
/// </remarks>
public sealed class DeterministicMemoryAbstractor : IMemoryAbstractor
{
    /// <summary>Number of times the abstractor was invoked (the AbstractOnly/Full write-time cost signal).</summary>
    public int Calls { get; private set; }

    /// <inheritdoc />
    public Task<MemoryAbstraction> AbstractAsync(string content, CancellationToken cancellationToken = default)
    {
        Calls++;

        // Compute the significant-token list once and reuse it for both the abstraction and the anchors.
        var significant = EvalTokens.Significant(content).ToList();
        var abstraction = significant.Count > 0
            ? string.Join(' ', significant.Take(3))
            : content.Trim();

        // Cue anchors: a couple of adjacent significant-word bigrams, a crude [Entity]+[Aspect] stand-in.
        var anchors = significant
            .Zip(significant.Skip(1), (a, b) => $"{a} {b}")
            .Take(2)
            .ToList();

        return Task.FromResult(new MemoryAbstraction { Abstraction = abstraction, CueAnchors = anchors });
    }
}

/// <summary>
/// Offline, LLM-free <see cref="IMemoryConsolidator"/> for the harmonic write-eval. Merges the candidate into
/// the most token-overlapping existing entry above a fixed threshold, else creates new — a deterministic
/// stand-in for the LLM consolidator's merge-vs-create judgement.
/// </summary>
public sealed class DeterministicMemoryConsolidator : IMemoryConsolidator
{
    private const double MergeThreshold = 0.34;

    /// <summary>Number of times the consolidator was invoked (the Full-mode incremental cost signal).</summary>
    public int Calls { get; private set; }

    /// <inheritdoc />
    public Task<MemoryConsolidationDecision> ConsolidateAsync(
        MemoryAbstraction candidate,
        string candidateValue,
        IReadOnlyList<ExistingMemory> similarExisting,
        CancellationToken cancellationToken = default)
    {
        // No candidates => no consolidation work and (for the LLM sibling) no API call, so it isn't counted.
        if (similarExisting.Count == 0)
            return Task.FromResult(MemoryConsolidationDecision.Create());

        Calls++;

        var candidateTokens = EvalTokens.Set(candidate.Abstraction);
        var best = similarExisting
            .Select(e => (Entry: e, Score: EvalTokens.Jaccard(candidateTokens, EvalTokens.Set(e.Abstraction))))
            .OrderByDescending(x => x.Score)
            .First();

        return Task.FromResult(best.Score >= MergeThreshold
            ? MemoryConsolidationDecision.MergeInto(best.Entry.Id)
            : MemoryConsolidationDecision.Create());
    }
}

/// <summary>Small tokenization helpers shared by the deterministic eval providers.</summary>
internal static class EvalTokens
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "are", "was", "were", "has", "have", "had", "with", "that", "this", "from",
        "you", "your", "our", "her", "his", "their", "any", "all", "not", "but", "who", "why", "how",
        "into", "out", "off", "get", "got", "will", "would", "can", "could", "should", "please", "always",
        "also", "just", "like", "want", "need", "dont", "don", "isnt", "its", "there", "here", "when", "then",
    };

    private static readonly char[] Separators =
        [' ', '\t', '\n', '\r', '-', '_', '.', ',', ':', ';', '/', '\\', '(', ')', '[', ']', '{', '}', '"', '\'', '!', '?'];

    /// <summary>Significant lowercased tokens in original order: length &gt; 3, not a stopword, deduped.</summary>
    public static IEnumerable<string> Significant(string text)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in text.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.ToLowerInvariant();
            if (token.Length > 3 && !StopWords.Contains(token) && seen.Add(token))
                yield return token;
        }
    }

    /// <summary>The distinct significant-token set of a string.</summary>
    public static HashSet<string> Set(string text) => new(Significant(text), StringComparer.Ordinal);

    /// <summary>Jaccard similarity between two token sets, in [0, 1].</summary>
    public static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
            return 0;

        var intersection = a.Count(b.Contains);
        var union = a.Count + b.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }
}
