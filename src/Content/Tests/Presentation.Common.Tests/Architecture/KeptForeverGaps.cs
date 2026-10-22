namespace Presentation.Common.Tests.Architecture;

/// <summary>
/// The stores that still declare <c>RetentionBasis.Unbounded</c>: nothing deletes what they hold (#901). Read by
/// <see cref="EveryPersonalDataStoreIsDeclaredTests"/>.
/// </summary>
/// <remarks>
/// This list only shrinks. A new store may not join it, and a store that gains a retention period, or a written reason to
/// be kept, must leave it, or the guard fails. Each entry says what will close the gap.
/// </remarks>
internal static class KeptForeverGaps
{
    /// <summary>The gaps, by full type name, with what closes each one.</summary>
    public static readonly IReadOnlyDictionary<string, string> Entries = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // On by default.

        // Knowledge graph and memory.
        // Personal knowledge in these now ages out; what remains unbounded is content extracted from documents, which waits
        // on the same decision as the document indexes below.
        ["Infrastructure.AI.RAG.GraphRag.KuzuGraphBackend"] = "document-derived GraphRAG content follows its documents",
        ["Infrastructure.AI.KnowledgeGraph.PostgreSql.PostgreSqlGraphStore"] = "document-derived nodes follow their documents",
        ["Infrastructure.AI.KnowledgeGraph.Neo4j.Neo4jGraphStore"] = "document-derived nodes follow their documents",

        // Off by default, file-based, or external.
        ["Infrastructure.AI.RAG.Retrieval.AzureAISearchVectorStore"] = "kept as long as its source document, or an ingest-time expiry",
        ["Infrastructure.AI.RAG.Retrieval.AzureAISearchBm25Store"] = "kept as long as its source document, or an ingest-time expiry",
    };
}
