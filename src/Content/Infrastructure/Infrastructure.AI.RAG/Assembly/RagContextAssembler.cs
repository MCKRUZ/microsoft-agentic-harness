using System.Diagnostics;
using System.Text;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Models;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.RAG.Assembly;

/// <summary>
/// Assembles final RAG context from reranked results by running pointer expansion,
/// sorting by relevance, enforcing a token budget, formatting with section headers,
/// and tracking citation spans. Creates a fresh <see cref="CitationTracker"/> per call
/// to avoid lifecycle coupling with a scoped dependency.
/// </summary>
/// <remarks>
/// Token estimation uses the chars/4 heuristic aligned with the generation model.
/// The assembler greedily includes chunks in rerank-score order until the budget
/// is exhausted, then sets <see cref="RagAssembledContext.WasTruncated"/>.
/// </remarks>
public sealed class RagContextAssembler : IRagContextAssembler
{
    private static readonly ActivitySource ActivitySource = new("AgenticHarness.RAG.Assembly");
    private const int CharsPerToken = 4;

    private readonly IPointerExpander _pointerExpander;
    private readonly ILogger<RagContextAssembler> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RagContextAssembler"/> class.
    /// </summary>
    /// <param name="pointerExpander">Expands chunks to include sibling/parent sections.</param>
    /// <param name="logger">Logger for recording assembly progress and budget decisions.</param>
    public RagContextAssembler(
        IPointerExpander pointerExpander,
        ILogger<RagContextAssembler> logger)
    {
        _pointerExpander = pointerExpander;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<RagAssembledContext> AssembleAsync(
        IReadOnlyList<RerankedResult> results,
        int maxTokens,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("rag.assembly.assemble_context");
        activity?.SetTag("rag.assembly.input_results", results.Count);
        activity?.SetTag("rag.assembly.max_tokens", maxTokens);

        // Extract chunks for pointer expansion
        var chunks = results
            .Select(r => r.RetrievalResult.Chunk)
            .ToList();

        // Step 1: Pointer expansion
        var expandedChunks = await _pointerExpander.ExpandAsync(chunks, cancellationToken);

        // Build a score lookup from reranked results (expanded siblings get 0 score)
        var scoreByChunkId = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var result in results)
        {
            scoreByChunkId.TryAdd(result.RetrievalResult.Chunk.Id, result.RerankScore);
        }

        // Step 2: Sort by rerank score descending
        var sorted = expandedChunks
            .OrderByDescending(c => scoreByChunkId.GetValueOrDefault(c.Id, 0.0))
            .ToList();

        // Step 3: Accumulate within token budget
        var citationTracker = new CitationTracker();
        var builder = new StringBuilder();
        var maxChars = maxTokens * CharsPerToken;
        var wasTruncated = false;
        var includedCount = 0;

        foreach (var chunk in sorted)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var score = scoreByChunkId.GetValueOrDefault(chunk.Id, 0.0);
            var header = $"--- Source: {chunk.SectionPath} (score: {score:F2}) ---\n";
            var section = $"{header}{chunk.Content}\n\n";

            if (builder.Length + section.Length > maxChars)
            {
                wasTruncated = true;
                _logger.LogDebug(
                    "Token budget exhausted after {IncludedCount} chunks; {RemainingCount} chunks dropped",
                    includedCount, sorted.Count - includedCount);
                break;
            }

            // Track the content portion (excluding header) for citations
            var contentOffset = builder.Length + header.Length;
            citationTracker.Track(chunk, contentOffset, chunk.Content.Length);

            builder.Append(section);
            includedCount++;
        }

        var assembledText = builder.ToString();
        var totalTokens = EstimateTokens(assembledText);
        var citations = citationTracker.GetCitations();

        activity?.SetTag("rag.assembly.included_chunks", includedCount);
        activity?.SetTag("rag.assembly.total_tokens", totalTokens);
        activity?.SetTag("rag.assembly.was_truncated", wasTruncated);
        activity?.SetTag("rag.assembly.citation_count", citations.Count);

        _logger.LogInformation(
            "Context assembled: {IncludedChunks} chunks, {Tokens} tokens, truncated={Truncated}, citations={Citations}",
            includedCount, totalTokens, wasTruncated, citations.Count);

        return new RagAssembledContext
        {
            AssembledText = assembledText,
            Citations = citations,
            TotalTokens = totalTokens,
            WasTruncated = wasTruncated
        };
    }

    private static int EstimateTokens(string text) =>
        (int)Math.Ceiling((double)text.Length / CharsPerToken);
}
