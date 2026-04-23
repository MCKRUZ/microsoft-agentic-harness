namespace Domain.AI.RAG.Enums;

/// <summary>
/// Tracks the lifecycle of a document ingestion job through the RAG pipeline.
/// Each status represents a distinct processing phase, enabling progress reporting,
/// resumability after failures, and observability via telemetry.
/// </summary>
public enum IngestionStatus
{
    /// <summary>
    /// Job has been created but processing has not started.
    /// The document URI has been validated and queued.
    /// </summary>
    Pending,

    /// <summary>
    /// The document is being parsed from its source format (PDF, HTML, Markdown)
    /// into a normalized text representation with structural metadata.
    /// </summary>
    Parsing,

    /// <summary>
    /// The parsed text is being split into chunks using the configured
    /// <see cref="ChunkingStrategy"/>. Structural boundaries and overlap
    /// settings are applied during this phase.
    /// </summary>
    Chunking,

    /// <summary>
    /// Chunks are being enriched with contextual metadata — Anthropic-style
    /// contextual prefixes, parent section references, and sibling relationships.
    /// </summary>
    Enriching,

    /// <summary>
    /// Chunks are being sent to the embedding model to generate vector representations.
    /// This is typically the most time-consuming and cost-intensive phase.
    /// </summary>
    Embedding,

    /// <summary>
    /// Embedded chunks are being written to the vector store index.
    /// Includes any index rebuilding or optimization required by the provider.
    /// </summary>
    Indexing,

    /// <summary>
    /// All phases completed successfully. The document's chunks are searchable
    /// in the vector store.
    /// </summary>
    Completed,

    /// <summary>
    /// Processing failed at one of the preceding phases. The <c>ErrorMessage</c>
    /// on the <see cref="Models.IngestionJob"/> contains the failure details.
    /// </summary>
    Failed
}
