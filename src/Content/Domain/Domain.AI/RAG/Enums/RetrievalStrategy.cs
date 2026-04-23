namespace Domain.AI.RAG.Enums;

/// <summary>
/// Defines the retrieval approach used to find relevant chunks for a query.
/// Each strategy makes different trade-offs between latency, recall, and
/// the ability to handle complex reasoning patterns.
/// </summary>
public enum RetrievalStrategy
{
    /// <summary>
    /// Combines dense vector similarity with BM25 sparse keyword matching using
    /// Reciprocal Rank Fusion (RRF). The default strategy — good recall across
    /// both semantic and lexical matches with moderate latency.
    /// </summary>
    HybridVectorBm25,

    /// <summary>
    /// Traverses a knowledge graph built from entity and relationship extraction.
    /// Excels at multi-hop queries where the answer requires following connections
    /// between documents. Higher indexing cost, but superior for relational questions.
    /// </summary>
    GraphRag,

    /// <summary>
    /// Uses a RAPTOR-style recursive tree of summaries at increasing abstraction levels.
    /// Matches queries at the appropriate granularity — detailed chunks for specific
    /// questions, high-level summaries for thematic queries.
    /// </summary>
    RaptorTree,

    /// <summary>
    /// Generates multiple reformulations of the original query and fuses their
    /// retrieval results. Improves recall for ambiguous or poorly-phrased queries
    /// at the cost of additional LLM calls and retrieval rounds.
    /// </summary>
    MultiQueryFusion
}
