using Domain.AI.RAG.Enums;
using Domain.AI.RAG.Models;
using Domain.Common.Config;
using Microsoft.Extensions.Options;
using Moq;

namespace Infrastructure.AI.RAG.Tests.Helpers;

internal static class RagTestData
{
    public static DocumentChunk CreateChunk(
        string id = "chunk-1",
        string content = "Test content for the document chunk.",
        string sectionPath = "Section > Subsection",
        string documentId = "doc-1",
        string? parentSectionId = null,
        IReadOnlyList<string>? siblingChunkIds = null) =>
        new()
        {
            Id = id,
            DocumentId = documentId,
            SectionPath = sectionPath,
            Content = content,
            Tokens = content.Length / 4,
            Metadata = new ChunkMetadata
            {
                SourceUri = new Uri($"file:///docs/{documentId}.md"),
                CreatedAt = DateTimeOffset.UtcNow,
                ParentSectionId = parentSectionId,
                SiblingChunkIds = siblingChunkIds ?? []
            }
        };

    public static RetrievalResult CreateRetrievalResult(
        string id = "chunk-1",
        string content = "Retrieved content.",
        double denseScore = 0.9,
        double sparseScore = 0.3,
        double fusedScore = 0.85) =>
        new()
        {
            Chunk = CreateChunk(id, content),
            DenseScore = denseScore,
            SparseScore = sparseScore,
            FusedScore = fusedScore
        };

    public static RerankedResult CreateRerankedResult(
        string id = "chunk-1",
        string content = "Reranked content.",
        double rerankScore = 0.85,
        int originalRank = 1,
        int rerankRank = 1) =>
        new()
        {
            RetrievalResult = CreateRetrievalResult(id, content),
            RerankScore = rerankScore,
            OriginalRank = originalRank,
            RerankRank = rerankRank
        };

    public static IReadOnlyList<RetrievalResult> CreateRetrievalResults(int count)
    {
        var results = new List<RetrievalResult>();
        for (var i = 0; i < count; i++)
        {
            var score = 1.0 - (i * 0.1);
            results.Add(CreateRetrievalResult(
                id: $"chunk-{i + 1}",
                content: $"Content for chunk {i + 1} with enough text to be meaningful.",
                denseScore: score,
                sparseScore: score * 0.5,
                fusedScore: score * 0.9));
        }
        return results;
    }

    public static IReadOnlyList<RerankedResult> CreateRerankedResults(int count)
    {
        var results = new List<RerankedResult>();
        for (var i = 0; i < count; i++)
        {
            var score = 1.0 - (i * 0.1);
            results.Add(CreateRerankedResult(
                id: $"chunk-{i + 1}",
                content: $"Content for chunk {i + 1} with enough text to be meaningful.",
                rerankScore: score,
                originalRank: i + 1,
                rerankRank: i + 1));
        }
        return results;
    }

    public static CragEvaluation CreateAcceptEvaluation(double score = 0.85) =>
        new()
        {
            Action = CorrectionAction.Accept,
            RelevanceScore = score,
            Reasoning = "Results are highly relevant"
        };

    public static CragEvaluation CreateRefineEvaluation(double score = 0.5) =>
        new()
        {
            Action = CorrectionAction.Refine,
            RelevanceScore = score,
            Reasoning = "Results need refinement"
        };

    public static CragEvaluation CreateRejectEvaluation(double score = 0.2) =>
        new()
        {
            Action = CorrectionAction.Reject,
            RelevanceScore = score,
            Reasoning = "Results are not relevant"
        };

    public static IOptionsMonitor<AppConfig> CreateConfigMonitor(
        Action<AppConfig>? configure = null)
    {
        var appConfig = new AppConfig();
        appConfig.AI.Rag.Retrieval.TopK = 10;
        appConfig.AI.Rag.Retrieval.RerankTopK = 5;
        appConfig.AI.Rag.Retrieval.RrfK = 60.0;
        appConfig.AI.Rag.Retrieval.EnableHybrid = true;
        appConfig.AI.Rag.Crag.Enabled = true;
        appConfig.AI.Rag.Crag.AcceptThreshold = 0.7;
        appConfig.AI.Rag.Crag.RefineThreshold = 0.4;
        appConfig.AI.Rag.Crag.AllowWebFallback = false;
        appConfig.AI.Rag.QueryTransform.EnableClassification = false;
        appConfig.AI.Rag.QueryTransform.EnableRagFusion = false;
        appConfig.AI.Rag.QueryTransform.EnableHyde = false;

        configure?.Invoke(appConfig);

        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(appConfig);
        return monitor.Object;
    }
}
