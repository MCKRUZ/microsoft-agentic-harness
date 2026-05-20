namespace Domain.Common.Config.AI.RAG;

/// <summary>
/// Root configuration for the RAG (Retrieval-Augmented Generation) pipeline
/// including document ingestion, retrieval, reranking, query transformation,
/// and model tiering. Bound from <c>AppConfig:AI:Rag</c> in appsettings.json.
/// </summary>
/// <remarks>
/// <para>
/// Configuration hierarchy:
/// <code>
/// AppConfig.AI.Rag
/// ├── Ingestion          — Chunking strategy, token targets, RAPTOR summaries
/// ├── Retrieval          — Top-K, RRF constant, hybrid search toggle
/// ├── VectorStore        — Provider, endpoint, embedding model, dimensions
/// ├── GraphRag           — Graph provider, community level, entity extraction
/// ├── Reranker           — Reranking strategy and model selection
/// ├── Crag               — Corrective RAG thresholds and web fallback
/// ├── QueryTransform     — RAG-Fusion, HyDE, query classification toggles
/// ├── ModelTiering       — Per-operation model tier assignment
/// ├── ComplexityRouting  — Cost-aware tier selection and pipeline optimization
/// ├── MultiHop           — Iterative multi-hop retrieval for complex queries
/// ├── Faithfulness       — Post-assembly answer faithfulness evaluation
/// ├── GraphDatabase      — Graph database backend configuration
/// ├── CrossSessionMemory — Cross-session knowledge persistence
/// ├── MultiSource        — Multi-source retrieval orchestration
/// ├── QualityGate        — CI/CD retrieval quality gates
/// ├── WebSearch          — Web search retrieval source
/// └── SqlDatabase        — SQL database retrieval source (disabled by default)
/// </code>
/// </para>
/// </remarks>
public class RagConfig
{
    /// <summary>
    /// Gets or sets the document ingestion configuration controlling chunking
    /// strategy, token targets, and RAPTOR hierarchical summarization.
    /// </summary>
    public IngestionConfig Ingestion { get; set; } = new();

    /// <summary>
    /// Gets or sets the retrieval configuration controlling result count,
    /// reciprocal rank fusion, and hybrid search.
    /// </summary>
    public RetrievalConfig Retrieval { get; set; } = new();

    /// <summary>
    /// Gets or sets the vector store configuration including provider,
    /// endpoint, embedding model, and index settings.
    /// </summary>
    public VectorStoreConfig VectorStore { get; set; } = new();

    /// <summary>
    /// Gets or sets the GraphRAG configuration for graph-based retrieval
    /// using community summaries and entity relationships.
    /// </summary>
    public GraphRagConfig GraphRag { get; set; } = new();

    /// <summary>
    /// Gets or sets the reranker configuration controlling post-retrieval
    /// relevance reranking strategy and model.
    /// </summary>
    public RerankerConfig Reranker { get; set; } = new();

    /// <summary>
    /// Gets or sets the Corrective RAG (CRAG) configuration controlling
    /// relevance thresholds and fallback behavior.
    /// </summary>
    public CragConfig Crag { get; set; } = new();

    /// <summary>
    /// Gets or sets the query transformation configuration for RAG-Fusion,
    /// HyDE, and automatic query classification.
    /// </summary>
    public QueryTransformConfig QueryTransform { get; set; } = new();

    /// <summary>
    /// Gets or sets the model tiering configuration for routing RAG operations
    /// to appropriate model tiers based on complexity.
    /// </summary>
    public ModelTieringConfig ModelTiering { get; set; } = new();

    /// <summary>
    /// Gets or sets the complexity-based query routing configuration controlling
    /// cost-aware tier selection and pipeline optimization.
    /// </summary>
    public ComplexityRoutingConfig ComplexityRouting { get; set; } = new();

    /// <summary>Gets or sets the multi-hop iterative retrieval configuration.</summary>
    public MultiHopConfig MultiHop { get; set; } = new();

    /// <summary>Gets or sets the faithfulness evaluation configuration.</summary>
    public FaithfulnessConfig Faithfulness { get; set; } = new();

    /// <summary>Graph database backend configuration for production knowledge graph storage.</summary>
    public GraphDatabaseConfig GraphDatabase { get; set; } = new();

    /// <summary>Cross-session memory configuration for knowledge persistence across conversations.</summary>
    public CrossSessionMemoryConfig CrossSessionMemory { get; set; } = new();

    /// <summary>
    /// Multi-source orchestration configuration for fanning out
    /// retrieval queries across vector, graph, and web sources.
    /// </summary>
    public MultiSourceConfig MultiSource { get; set; } = new();

    /// <summary>
    /// CI/CD quality gate configuration for enforcing minimum
    /// retrieval quality thresholds via Ragas-style evaluation.
    /// </summary>
    public QualityGateConfig QualityGate { get; set; } = new();

    /// <summary>Web search retrieval source configuration.</summary>
    public WebSearchConfig WebSearch { get; set; } = new();

    /// <summary>SQL database retrieval source configuration (disabled by default).</summary>
    public SqlDatabaseConfig SqlDatabase { get; set; } = new();
}
