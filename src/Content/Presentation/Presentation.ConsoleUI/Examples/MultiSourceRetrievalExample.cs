using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Enums;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Presentation.ConsoleUI.Common.Helpers;
using Spectre.Console;

namespace Presentation.ConsoleUI.Examples;

/// <summary>
/// Demonstrates parallel multi-source retrieval orchestration across vector, graph,
/// and web search sources with retrieval cost tracking. Shows how query complexity
/// drives source selection and cost efficiency gains.
/// </summary>
public class MultiSourceRetrievalExample
{
    private readonly IMultiSourceOrchestrator _orchestrator;
    private readonly IRetrievalCostTracker _costTracker;
    private readonly ILogger<MultiSourceRetrievalExample> _logger;

    public MultiSourceRetrievalExample(
        IMultiSourceOrchestrator orchestrator,
        IRetrievalCostTracker costTracker,
        ILogger<MultiSourceRetrievalExample> logger)
    {
        _orchestrator = orchestrator;
        _costTracker = costTracker;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ConsoleHelper.DisplayHeader("Multi-Source Retrieval", Color.Cyan);
            ConsoleHelper.DisplayModeInfo(isLive: false, "In-memory vector + graph stores");

            if (!AnsiConsole.Profile.Capabilities.Interactive)
            {
                await RunHeadlessAsync(cancellationToken);
                return;
            }

            var mode = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]Select scenario:[/]")
                    .AddChoices(
                        "Simple Query (Vector Only)",
                        "Complex Query (All Sources)",
                        "Cost Comparison",
                        "Back"));

            switch (mode)
            {
                case "Simple Query (Vector Only)":
                    await RunSimpleQueryAsync(cancellationToken);
                    break;
                case "Complex Query (All Sources)":
                    await RunComplexQueryAsync(cancellationToken);
                    break;
                case "Cost Comparison":
                    await RunCostComparisonAsync(cancellationToken);
                    break;
            }
        }
        catch (Exception ex)
        {
            ConsoleHelper.DisplayError($"Demo failed: {ex.Message}");
            _logger.LogError(ex, "MultiSourceRetrievalExample failed");
        }
    }

    private async Task RunHeadlessAsync(CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[grey]Running multi-source retrieval scenarios...[/]");
        await RunSimpleQueryAsync(cancellationToken);
        await RunComplexQueryAsync(cancellationToken);
        await RunCostComparisonAsync(cancellationToken);
    }

    private async Task RunSimpleQueryAsync(CancellationToken cancellationToken)
    {
        ConsoleHelper.DisplayStep(1, 3, "Simple Query (Vector Only)");
        AnsiConsole.MarkupLine("[grey]Query complexity: Simple → Vector search only, no reranking[/]\n");

        _costTracker.Reset();
        const string simpleQuery = "What is Clean Architecture?";

        try
        {
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("cyan"))
                .StartAsync("Retrieving from vector store...", async _ =>
                {
                    var results = await _orchestrator.RetrieveFromAllSourcesAsync(
                        simpleQuery,
                        topK: 5,
                        QueryComplexity.Simple,
                        cancellationToken);

                    DisplayRetrievalResults(results, "Vector Store");
                });
        }
        catch (Exception ex)
        {
            ConsoleHelper.DisplayError($"Simple query failed: {ex.Message}");
            _logger.LogError(ex, "Simple retrieval query failed");
        }
    }

    private async Task RunComplexQueryAsync(CancellationToken cancellationToken)
    {
        ConsoleHelper.DisplayStep(2, 3, "Complex Query (All Sources)");
        AnsiConsole.MarkupLine("[grey]Query complexity: Complex → Vector + Graph + Web in parallel[/]\n");

        _costTracker.Reset();
        const string complexQuery = "How does the MediatR pipeline integrate with governance?";

        try
        {
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("cyan"))
                .StartAsync("Retrieving from all sources...", async _ =>
                {
                    var results = await _orchestrator.RetrieveFromAllSourcesAsync(
                        complexQuery,
                        topK: 10,
                        QueryComplexity.Complex,
                        cancellationToken);

                    DisplayRetrievalResults(results, "Vector + Graph + Web");
                });
        }
        catch (Exception ex)
        {
            ConsoleHelper.DisplayError($"Complex query failed: {ex.Message}");
            _logger.LogError(ex, "Complex retrieval query failed");
        }
    }

    private async Task RunCostComparisonAsync(CancellationToken cancellationToken)
    {
        ConsoleHelper.DisplayStep(3, 3, "Cost Comparison");
        AnsiConsole.MarkupLine("[grey]Comparing retrieval costs across complexity levels[/]\n");

        // Simple query cost
        _costTracker.Reset();
        try
        {
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("cyan"))
                .StartAsync("Running simple query...", async _ =>
                {
                    await _orchestrator.RetrieveFromAllSourcesAsync(
                        "What is Clean Architecture?",
                        topK: 5,
                        QueryComplexity.Simple,
                        cancellationToken);
                });
        }
        catch (Exception ex)
        {
            ConsoleHelper.DisplayError($"Cost comparison simple query failed: {ex.Message}");
        }

        var simpleCost = _costTracker.GetSummary();

        // Complex query cost
        _costTracker.Reset();
        try
        {
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("cyan"))
                .StartAsync("Running complex query...", async _ =>
                {
                    await _orchestrator.RetrieveFromAllSourcesAsync(
                        "How does the MediatR pipeline integrate with governance?",
                        topK: 10,
                        QueryComplexity.Complex,
                        cancellationToken);
                });
        }
        catch (Exception ex)
        {
            ConsoleHelper.DisplayError($"Cost comparison complex query failed: {ex.Message}");
        }

        var complexCost = _costTracker.GetSummary();

        // Display comparison table
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("[bold]Metric[/]");
        table.AddColumn("[bold]Simple Query[/]");
        table.AddColumn("[bold]Complex Query[/]");
        table.AddColumn("[bold]Overhead[/]");

        table.AddRow(
            "Retrieval Calls",
            simpleCost.RetrievalCalls.ToString(),
            complexCost.RetrievalCalls.ToString(),
            $"{((double)(complexCost.RetrievalCalls - simpleCost.RetrievalCalls) / Math.Max(simpleCost.RetrievalCalls, 1) * 100):F1}%");

        table.AddRow(
            "Prompt Tokens",
            simpleCost.PromptTokens.ToString(),
            complexCost.PromptTokens.ToString(),
            $"{((double)(complexCost.PromptTokens - simpleCost.PromptTokens) / Math.Max(simpleCost.PromptTokens, 1) * 100):F1}%");

        table.AddRow(
            "Completion Tokens",
            simpleCost.CompletionTokens.ToString(),
            complexCost.CompletionTokens.ToString(),
            $"{((double)(complexCost.CompletionTokens - simpleCost.CompletionTokens) / Math.Max(simpleCost.CompletionTokens, 1) * 100):F1}%");

        table.AddRow(
            "Total Latency",
            FormatTimespan(simpleCost.TotalLatency),
            FormatTimespan(complexCost.TotalLatency),
            $"{((double)(complexCost.TotalLatency.TotalMilliseconds - simpleCost.TotalLatency.TotalMilliseconds) / Math.Max(simpleCost.TotalLatency.TotalMilliseconds, 1) * 100):F1}%");

        table.AddRow(
            "Est. Cost (USD)",
            $"${simpleCost.EstimatedCost:F4}",
            $"${complexCost.EstimatedCost:F4}",
            $"${complexCost.EstimatedCost - simpleCost.EstimatedCost:F4}");

        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine("\n[grey]Note: With empty in-memory stores, both queries return no results.[/]");
        AnsiConsole.MarkupLine("[grey]Cost differences reflect API call overhead and configuration-driven routing.[/]");

        ConsoleHelper.DisplaySuccess("Cost comparison complete");
    }

    private void DisplayRetrievalResults(IReadOnlyList<Domain.AI.RAG.Models.RetrievalResult> results, string source)
    {
        if (results.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No results from {source}.[/]");
            AnsiConsole.MarkupLine("[grey]This is expected with in-memory/empty vector stores.[/]");
            AnsiConsole.MarkupLine("[grey]In production, results would be populated from configured stores.[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("[bold]Chunk ID[/]");
        table.AddColumn("[bold]Section[/]");
        table.AddColumn("[bold]Dense Score[/]");
        table.AddColumn("[bold]Sparse Score[/]");
        table.AddColumn("[bold]Fused Score[/]");
        table.AddColumn("[bold]Content Preview[/]");

        foreach (var result in results.Take(5))
        {
            var preview = result.Chunk.Content.Length > 60
                ? result.Chunk.Content[..60] + "..."
                : result.Chunk.Content;

            table.AddRow(
                result.Chunk.Id,
                result.Chunk.SectionPath,
                $"{result.DenseScore:F3}",
                $"{result.SparseScore:F3}",
                $"{result.FusedScore:F3}",
                Markup.Escape(preview));
        }

        AnsiConsole.Write(table);

        if (results.Count > 5)
        {
            AnsiConsole.MarkupLine($"[grey]... and {results.Count - 5} more results[/]");
        }
    }

    private static string FormatTimespan(TimeSpan timespan)
    {
        if (timespan.TotalSeconds < 1)
            return $"{timespan.TotalMilliseconds:F0}ms";
        return $"{timespan.TotalSeconds:F2}s";
    }
}
