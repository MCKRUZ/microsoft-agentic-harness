using Domain.AI.RAG.Enums;

namespace Domain.AI.RAG.Models;

/// <summary>
/// Tracks the lifecycle and progress of a document ingestion job through the
/// RAG pipeline. Each job processes a single document through parsing, chunking,
/// enrichment, embedding, and indexing phases. The status field enables progress
/// reporting, resumability after transient failures, and observability dashboards.
/// </summary>
public record IngestionJob
{
    /// <summary>
    /// Unique identifier for this ingestion job, used for idempotency checks
    /// and progress tracking across pipeline phases.
    /// </summary>
    public required string JobId { get; init; }

    /// <summary>
    /// The URI of the document being ingested (file path, blob URL, or web URL).
    /// This is the input to the parsing phase.
    /// </summary>
    public required Uri DocumentUri { get; init; }

    /// <summary>
    /// The current processing phase of this job. Transitions linearly from
    /// <see cref="IngestionStatus.Pending"/> through to <see cref="IngestionStatus.Completed"/>
    /// or <see cref="IngestionStatus.Failed"/> on error.
    /// </summary>
    public required IngestionStatus Status { get; init; }

    /// <summary>
    /// The number of chunks produced during the chunking phase.
    /// Zero until chunking completes. Used for progress reporting and
    /// estimating embedding costs.
    /// </summary>
    public required int ChunksProduced { get; init; }

    /// <summary>
    /// The number of embeddings successfully generated during the embedding phase.
    /// Should equal <see cref="ChunksProduced"/> on successful completion.
    /// A mismatch indicates partial failure during embedding.
    /// </summary>
    public required int EmbeddingsGenerated { get; init; }

    /// <summary>
    /// When this ingestion job was created and queued for processing.
    /// </summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// When this ingestion job finished processing (either successfully or with failure).
    /// Null while the job is still in progress.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>
    /// The error message if the job failed. Null for successful or in-progress jobs.
    /// Contains the exception message from the phase that failed, enabling
    /// targeted retry of the specific failed phase.
    /// </summary>
    public string? ErrorMessage { get; init; }
}
