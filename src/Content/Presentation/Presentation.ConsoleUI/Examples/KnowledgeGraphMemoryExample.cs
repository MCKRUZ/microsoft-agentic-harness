using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Microsoft.Extensions.Logging;
using Presentation.ConsoleUI.Common.Helpers;
using Spectre.Console;

namespace Presentation.ConsoleUI.Examples;

/// <summary>
/// Demonstrates the Knowledge Graph memory lifecycle: Remember, Recall, Forget, and Improve
/// operations with session caching and feedback-weighted learning.
/// </summary>
/// <remarks>
/// <para>
/// The Knowledge Graph implements a two-tier memory architecture:
/// - Session cache (in-memory, fast) for facts learned in the current conversation
/// - Permanent graph store (persistent, queryable) for cross-session knowledge
/// </para>
/// <para>
/// Feedback learning updates node weights via exponential moving average,
/// allowing the graph to improve recall quality over time based on user interactions.
/// </para>
/// </remarks>
public class KnowledgeGraphMemoryExample
{
    private readonly IKnowledgeMemory _knowledgeMemory;
    private readonly ISessionKnowledgeCache _sessionCache;
    private readonly IFeedbackStore _feedbackStore;
    private readonly ILogger<KnowledgeGraphMemoryExample> _logger;

    public KnowledgeGraphMemoryExample(
        IKnowledgeMemory knowledgeMemory,
        ISessionKnowledgeCache sessionCache,
        IFeedbackStore feedbackStore,
        ILogger<KnowledgeGraphMemoryExample> logger)
    {
        _knowledgeMemory = knowledgeMemory;
        _sessionCache = sessionCache;
        _feedbackStore = feedbackStore;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        ConsoleHelper.DisplayHeader("Knowledge Graph Memory", Color.DodgerBlue1);
        ConsoleHelper.DisplayModeInfo(isLive: false);

        try
        {
            // Step 1: Remember facts
            ConsoleHelper.DisplayStep(1, 6, "Remember Facts");
            await RememberFactsAsync(cancellationToken);

            // Step 2: Recall by query
            ConsoleHelper.DisplayStep(2, 6, "Recall by Query");
            await RecallByQueryAsync(cancellationToken);

            // Step 3: Improve from feedback
            ConsoleHelper.DisplayStep(3, 6, "Improve from Feedback");
            await ImproveFromFeedbackAsync(cancellationToken);

            // Step 4: Session cache operations
            ConsoleHelper.DisplayStep(4, 6, "Session Cache Operations");
            await SessionCacheOperationsAsync();

            // Step 5: Forget operation
            ConsoleHelper.DisplayStep(5, 6, "Forget Operation");
            await ForgetOperationAsync(cancellationToken);

            // Step 6: Feedback weights
            ConsoleHelper.DisplayStep(6, 6, "Feedback Weights & Learning");
            await FeedbackWeightsAsync(cancellationToken);

            ConsoleHelper.DisplaySuccess("Knowledge Graph memory lifecycle demonstration complete");
        }
        catch (Exception ex)
        {
            ConsoleHelper.DisplayError($"Error: {ex.Message}");
            _logger.LogError(ex, "Knowledge Graph example failed");
        }
    }

    private async Task RememberFactsAsync(CancellationToken cancellationToken)
    {
        var facts = new[]
        {
            ("claude-ai", "Claude is an AI assistant made by Anthropic", "Person"),
            ("rag-system", "RAG systems combine retrieval and generation for contextual responses", "Technology"),
            ("knowledge-graphs", "Knowledge graphs organize facts as nodes and edges for semantic reasoning", "Concept"),
            ("feedback-learning", "Feedback-based learning improves graph quality through user interactions", "Concept"),
        };

        foreach (var (key, content, entityType) in facts)
        {
            await _knowledgeMemory.RememberAsync(key, content, entityType, cancellationToken);
            AnsiConsole.MarkupLine($"  [green]✓[/] Remembered: [white]{key}[/] ([grey]{entityType}[/])");
        }
    }

    private async Task RecallByQueryAsync(CancellationToken cancellationToken)
    {
        var queries = new[] { "AI assistant", "retrieval generation", "semantic reasoning" };

        foreach (var query in queries)
        {
            var results = await _knowledgeMemory.RecallAsync(query, maxResults: 5, cancellationToken: cancellationToken);
            AnsiConsole.MarkupLine($"\n  [bold]Query:[/] [white]{query}[/] → [grey]{results.Count} result(s)[/]");

            if (results.Count > 0)
            {
                var table = new Table().Border(TableBorder.Rounded);
                table.AddColumn("[bold]Node ID[/]");
                table.AddColumn("[bold]Type[/]");
                table.AddColumn("[bold]Name[/]");

                foreach (var node in results)
                {
                    table.AddRow(
                        Markup.Escape(node.Id[..Math.Min(16, node.Id.Length)]) + (node.Id.Length > 16 ? "..." : ""),
                        Markup.Escape(node.Type),
                        Markup.Escape(node.Name));
                }

                AnsiConsole.Write(table);
            }
        }
    }

    private async Task ImproveFromFeedbackAsync(CancellationToken cancellationToken)
    {
        var queryResults = await _knowledgeMemory.RecallAsync("AI", maxResults: 3, cancellationToken: cancellationToken);

        if (queryResults.Count == 0)
        {
            AnsiConsole.MarkupLine("  [yellow]⚠[/] No nodes found for feedback improvement");
            return;
        }

        var relevantNodeIds = queryResults.Select(n => n.Id).ToList();
        var userMessage = "That explanation of Claude was very helpful and accurate.";
        var assistantResponse = "Claude is an AI assistant made by Anthropic with strong reasoning capabilities.";

        await _knowledgeMemory.ImproveAsync(userMessage, assistantResponse, relevantNodeIds, cancellationToken);

        AnsiConsole.MarkupLine($"  [green]✓[/] Applied feedback to {relevantNodeIds.Count} relevant node(s)");
        AnsiConsole.MarkupLine($"  [grey]User:[/] {Markup.Escape(userMessage)}");
        AnsiConsole.MarkupLine($"  [grey]Assistant:[/] {Markup.Escape(assistantResponse)}");
    }

    private async Task SessionCacheOperationsAsync()
    {
        var testNode = new GraphNode
        {
            Id = "session-test-node-001",
            Name = "Session Test Entity",
            Type = "Temporary",
            Properties = new Dictionary<string, string> { { "source", "session-cache-demo" } },
            ChunkIds = ["chunk-001"],
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _sessionCache.Add(testNode);
        AnsiConsole.MarkupLine($"  [green]✓[/] Added node to session cache: [white]{testNode.Name}[/]");
        AnsiConsole.MarkupLine($"  [grey]Cache count: {_sessionCache.Count}[/]");

        var searchResults = _sessionCache.Search("Session Test", maxResults: 5);
        AnsiConsole.MarkupLine($"  [green]✓[/] Session cache search found: [white]{searchResults.Count}[/] node(s)");

        _sessionCache.Remove(testNode.Id);
        AnsiConsole.MarkupLine($"  [green]✓[/] Removed node from cache");
        AnsiConsole.MarkupLine($"  [grey]Cache count after removal: {_sessionCache.Count}[/]");
    }

    private async Task ForgetOperationAsync(CancellationToken cancellationToken)
    {
        await _knowledgeMemory.ForgetAsync("rag-system", cancellationToken);
        AnsiConsole.MarkupLine("  [green]✓[/] Forgot memory: [white]rag-system[/]");

        var results = await _knowledgeMemory.RecallAsync("RAG", maxResults: 5, cancellationToken: cancellationToken);
        AnsiConsole.MarkupLine($"  [grey]Recall after forget: {results.Count} result(s) (should be 0 if unique)[/]");
    }

    private async Task FeedbackWeightsAsync(CancellationToken cancellationToken)
    {
        var nodeId = "claude-ai";

        var weightBefore = await _feedbackStore.GetNodeWeightAsync(nodeId, cancellationToken);
        AnsiConsole.MarkupLine("\n  [bold]Before feedback:[/]");
        DisplayWeightTable(new[] { weightBefore });

        await _feedbackStore.ApplyNodeFeedbackAsync(nodeId, feedbackScore: 4.5, alpha: 0.3, cancellationToken);

        var weightAfter = await _feedbackStore.GetNodeWeightAsync(nodeId, cancellationToken);
        AnsiConsole.MarkupLine("\n  [bold]After applying feedback (score=4.5, alpha=0.3):[/]");
        DisplayWeightTable(new[] { weightAfter });

        AnsiConsole.MarkupLine($"\n  [grey]Weight delta: {(weightAfter.Weight - weightBefore.Weight):+0.000;-0.000;0.000}[/]");
        AnsiConsole.MarkupLine($"  [grey]Update count: {weightBefore.UpdateCount} → {weightAfter.UpdateCount}[/]");
    }

    private static void DisplayWeightTable(IEnumerable<NodeFeedbackWeight> weights)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("[bold]Node ID[/]");
        table.AddColumn("[bold]Weight[/]");
        table.AddColumn("[bold]Updates[/]");
        table.AddColumn("[bold]Last Updated[/]");

        foreach (var w in weights)
        {
            table.AddRow(
                Markup.Escape(w.NodeId[..Math.Min(12, w.NodeId.Length)]) + (w.NodeId.Length > 12 ? "..." : ""),
                $"[white]{w.Weight:F3}[/]",
                w.UpdateCount.ToString(),
                w.LastUpdatedAt.ToString("yyyy-MM-dd HH:mm:ss UTC"));
        }

        AnsiConsole.Write(table);
    }
}
