namespace Domain.AI.RAG.Enums;

/// <summary>
/// Query complexity tier that determines the retrieval strategy cost/depth tradeoff.
/// </summary>
public enum QueryComplexity
{
    /// <summary>The LLM can answer directly from parametric knowledge. No retrieval needed.</summary>
    Trivial,

    /// <summary>Single-pass vector search suffices. Skip reranking and CRAG.</summary>
    Simple,

    /// <summary>Full hybrid pipeline with reranking and CRAG evaluation.</summary>
    Moderate,

    /// <summary>Multi-hop iterative retrieval with query decomposition (Phase B).</summary>
    Complex
}
