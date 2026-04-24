using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Models;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Presentation.ConsoleUI.Common.Helpers;
using Spectre.Console;

namespace Presentation.ConsoleUI.Examples;

/// <summary>
/// Demonstrates the RAG pipeline: component walkthrough with synthetic data and
/// optional live queries through the full orchestrator.
/// </summary>
public class RagPipelineExample
{
    private readonly IRagContextAssembler _assembler;
    private readonly IRagOrchestrator _orchestrator;
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly ILogger<RagPipelineExample> _logger;

    public RagPipelineExample(
        IRagContextAssembler assembler,
        IRagOrchestrator orchestrator,
        IOptionsMonitor<AppConfig> appConfig,
        ILogger<RagPipelineExample> logger)
    {
        _assembler = assembler;
        _orchestrator = orchestrator;
        _appConfig = appConfig;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        ConsoleHelper.DisplayHeader("RAG Pipeline", Color.MediumOrchid);

        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            await RunHeadlessAsync(cancellationToken);
            return;
        }

        var mode = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Select mode:[/]")
                .AddChoices("Component Walkthrough", "Live Pipeline Query", "Show Configuration", "Back"));

        switch (mode)
        {
            case "Component Walkthrough":
                await RunComponentWalkthroughAsync(cancellationToken);
                break;
            case "Live Pipeline Query":
                await RunLiveQueryAsync(cancellationToken);
                break;
            case "Show Configuration":
                ShowRagConfig();
                break;
        }
    }

    private async Task RunHeadlessAsync(CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[grey]Running RAG pipeline component walkthrough...[/]");
        await RunComponentWalkthroughAsync(cancellationToken);
    }

    private async Task RunComponentWalkthroughAsync(CancellationToken cancellationToken)
    {
        var chunks = CreateSyntheticChunks();

        AnsiConsole.MarkupLine("\n[bold mediumorchid]Step 1: Document Chunks (Ingestion Output)[/]");
        var chunkTable = new Table().Border(TableBorder.Rounded);
        chunkTable.AddColumn("ID");
        chunkTable.AddColumn("Section Path");
        chunkTable.AddColumn("Tokens");
        foreach (var chunk in chunks)
            chunkTable.AddRow(chunk.Id, chunk.SectionPath, chunk.Tokens.ToString());
        AnsiConsole.Write(chunkTable);

        AnsiConsole.MarkupLine("\n[bold mediumorchid]Step 2: Hybrid Retrieval (RRF Fusion)[/]");
        var retrievalResults = SimulateRetrieval(chunks);
        var rrfTable = new Table().Border(TableBorder.Rounded);
        rrfTable.AddColumn("Chunk");
        rrfTable.AddColumn("Dense Score");
        rrfTable.AddColumn("Sparse Score");
        rrfTable.AddColumn("RRF Fused");
        foreach (var r in retrievalResults)
            rrfTable.AddRow(r.Chunk.Id, $"{r.DenseScore:F3}", $"{r.SparseScore:F3}", $"{r.FusedScore:F3}");
        AnsiConsole.Write(rrfTable);
        AnsiConsole.MarkupLine("[grey]  Formula: score = 1/(k + rank_dense) + 1/(k + rank_sparse), k=60[/]");

        AnsiConsole.MarkupLine("\n[bold mediumorchid]Step 3: Reranking (Cross-Encoder)[/]");
        var rerankedResults = SimulateReranking(retrievalResults);
        var rerankTable = new Table().Border(TableBorder.Rounded);
        rerankTable.AddColumn("Chunk");
        rerankTable.AddColumn("Original Rank");
        rerankTable.AddColumn("Rerank Score");
        rerankTable.AddColumn("New Rank");
        foreach (var r in rerankedResults)
            rerankTable.AddRow(r.RetrievalResult.Chunk.Id, r.OriginalRank.ToString(), $"{r.RerankScore:F3}", r.RerankRank.ToString());
        AnsiConsole.Write(rerankTable);

        AnsiConsole.MarkupLine("\n[bold mediumorchid]Step 4: CRAG Evaluation[/]");
        var rag = _appConfig.CurrentValue.AI.Rag;
        AnsiConsole.MarkupLine($"  Accept threshold: [white]{rag.Crag.AcceptThreshold:F2}[/]  |  Refine threshold: [white]{rag.Crag.RefineThreshold:F2}[/]");
        AnsiConsole.MarkupLine("[green]  Simulated score: 0.85 → Action: Accept[/]");

        AnsiConsole.MarkupLine("\n[bold mediumorchid]Step 5: Context Assembly[/]");
        try
        {
            var assembled = await _assembler.AssembleAsync(rerankedResults, 2048, cancellationToken);

            AnsiConsole.MarkupLine($"  Tokens: [white]{assembled.TotalTokens}[/]  |  Truncated: [white]{assembled.WasTruncated}[/]  |  Citations: [white]{assembled.Citations.Count}[/]");

            if (assembled.Citations.Count > 0)
            {
                var citTable = new Table().Border(TableBorder.Rounded);
                citTable.AddColumn("Chunk ID");
                citTable.AddColumn("Section");
                citTable.AddColumn("Offset");
                citTable.AddColumn("Length");
                foreach (var cit in assembled.Citations)
                    citTable.AddRow(cit.ChunkId, cit.SectionPath, cit.StartOffset.ToString(), $"{cit.EndOffset - cit.StartOffset}");
                AnsiConsole.Write(citTable);
            }

            var preview = assembled.AssembledText.Length > 600
                ? assembled.AssembledText[..600] + "\n... (truncated for display)"
                : assembled.AssembledText;
            AnsiConsole.Write(new Panel(Markup.Escape(preview))
                .Header("[bold]Assembled Context[/]")
                .Border(BoxBorder.Rounded));
        }
        catch (Exception ex)
        {
            ConsoleHelper.DisplayError($"Assembly failed: {ex.Message}");
            _logger.LogError(ex, "Context assembly failed during walkthrough");
        }

        ConsoleHelper.DisplaySuccess("RAG pipeline walkthrough complete");
    }

    private async Task RunLiveQueryAsync(CancellationToken cancellationToken)
    {
        var query = AnsiConsole.Ask<string>("[bold]Enter your search query:[/]");

        try
        {
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("mediumorchid"))
                .StartAsync("Running RAG pipeline...", async _ =>
                {
                    var result = await _orchestrator.SearchAsync(query, cancellationToken: cancellationToken);

                    AnsiConsole.MarkupLine(
                        $"\n  Tokens: [white]{result.TotalTokens}[/]  |  Citations: [white]{result.Citations.Count}[/]  |  Truncated: [white]{result.WasTruncated}[/]");

                    var textPreview = result.AssembledText.Length > 800
                        ? result.AssembledText[..800] + "\n..."
                        : result.AssembledText;
                    AnsiConsole.Write(new Panel(Markup.Escape(textPreview))
                        .Header("[bold]RAG Result[/]")
                        .Border(BoxBorder.Rounded));
                });
        }
        catch (Exception ex)
        {
            ConsoleHelper.DisplayError($"Pipeline error: {ex.Message}");
            AnsiConsole.MarkupLine("[grey]Ensure vector stores and LLM endpoints are configured in appsettings.json.[/]");
        }
    }

    private void ShowRagConfig()
    {
        var rag = _appConfig.CurrentValue.AI.Rag;
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("[bold]Setting[/]");
        table.AddColumn("[bold]Value[/]");

        table.AddRow("Retrieval TopK", rag.Retrieval.TopK.ToString());
        table.AddRow("RRF K (fusion constant)", rag.Retrieval.RrfK.ToString("F1"));
        table.AddRow("Hybrid Enabled", rag.Retrieval.EnableHybrid.ToString());
        table.AddRow("Reranker Strategy", rag.Reranker.Strategy ?? "none");
        table.AddRow("CRAG Enabled", rag.Crag.Enabled.ToString());
        table.AddRow("CRAG Accept Threshold", rag.Crag.AcceptThreshold.ToString("F2"));
        table.AddRow("CRAG Refine Threshold", rag.Crag.RefineThreshold.ToString("F2"));
        table.AddRow("Classification Enabled", rag.QueryTransform.EnableClassification.ToString());
        table.AddRow("RAG Fusion Enabled", rag.QueryTransform.EnableRagFusion.ToString());
        table.AddRow("GraphRAG Enabled", rag.GraphRag.Enabled.ToString());

        AnsiConsole.Write(table);
    }

    private static IReadOnlyList<DocumentChunk> CreateSyntheticChunks() =>
    [
        new()
        {
            Id = "arch-1", DocumentId = "doc-architecture", SectionPath = "Architecture > Clean Architecture",
            Content = "Clean Architecture separates concerns into layers: Domain, Application, Infrastructure, and Presentation. Each layer has explicit dependencies flowing inward, ensuring the domain logic remains independent of frameworks and external services.",
            Tokens = 42,
            Metadata = new ChunkMetadata { SourceUri = new Uri("file:///docs/architecture.md"), CreatedAt = DateTimeOffset.UtcNow, ParentSectionId = "arch-parent", SiblingChunkIds = ["arch-2"] }
        },
        new()
        {
            Id = "arch-2", DocumentId = "doc-architecture", SectionPath = "Architecture > CQRS Pattern",
            Content = "CQRS splits read and write operations into separate models. Commands mutate state through handlers validated by FluentValidation pipeline behaviors. Queries return DTOs without side effects.",
            Tokens = 38,
            Metadata = new ChunkMetadata { SourceUri = new Uri("file:///docs/architecture.md"), CreatedAt = DateTimeOffset.UtcNow, ParentSectionId = "arch-parent", SiblingChunkIds = ["arch-1"] }
        },
        new()
        {
            Id = "rag-1", DocumentId = "doc-rag", SectionPath = "RAG > Hybrid Retrieval",
            Content = "Hybrid retrieval combines dense vector similarity with BM25 sparse keyword matching. Results are fused using Reciprocal Rank Fusion: score = 1/(k + rank_dense) + 1/(k + rank_sparse) where k=60.",
            Tokens = 40,
            Metadata = new ChunkMetadata { SourceUri = new Uri("file:///docs/rag-pipeline.md"), CreatedAt = DateTimeOffset.UtcNow }
        },
        new()
        {
            Id = "rag-2", DocumentId = "doc-rag", SectionPath = "RAG > CRAG Evaluation",
            Content = "Corrective RAG (CRAG) evaluates retrieval quality before generation. An LLM scores relevance 0-1. Above 0.7: Accept. Between 0.4-0.7: Refine the query and retry. Below 0.4: Reject the retrieval entirely.",
            Tokens = 44,
            Metadata = new ChunkMetadata { SourceUri = new Uri("file:///docs/rag-pipeline.md"), CreatedAt = DateTimeOffset.UtcNow }
        },
        new()
        {
            Id = "tools-1", DocumentId = "doc-tools", SectionPath = "Tools > Keyed DI Registration",
            Content = "Tools are registered using keyed dependency injection: AddKeyedSingleton<ITool>(toolName). This enables lazy resolution from skill declarations and runtime tool discovery via the service provider.",
            Tokens = 35,
            Metadata = new ChunkMetadata { SourceUri = new Uri("file:///docs/tool-system.md"), CreatedAt = DateTimeOffset.UtcNow }
        }
    ];

    private static IReadOnlyList<RetrievalResult> SimulateRetrieval(IReadOnlyList<DocumentChunk> chunks) =>
        chunks.Select((chunk, index) => new RetrievalResult
        {
            Chunk = chunk,
            DenseScore = 0.95 - (index * 0.08),
            SparseScore = 0.80 - (index * 0.12),
            FusedScore = 1.0 / (60 + index + 1) + 1.0 / (60 + index + 1)
        }).OrderByDescending(r => r.FusedScore).ToList();

    private static IReadOnlyList<RerankedResult> SimulateReranking(IReadOnlyList<RetrievalResult> results) =>
        results.Select((r, index) => new RerankedResult
        {
            RetrievalResult = r,
            RerankScore = 0.92 - (index * 0.07),
            OriginalRank = index + 1,
            RerankRank = index + 1
        }).OrderByDescending(r => r.RerankScore).ToList();
}
