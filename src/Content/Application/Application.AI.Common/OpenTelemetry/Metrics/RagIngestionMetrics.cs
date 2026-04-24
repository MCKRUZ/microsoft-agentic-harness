using System.Diagnostics.Metrics;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Telemetry;

namespace Application.AI.Common.OpenTelemetry.Metrics;

/// <summary>
/// OTel metric instruments for RAG document ingestion pipeline.
/// Tracks document parsing, chunking, embedding, and indexing operations.
/// </summary>
public static class RagIngestionMetrics
{
	/// <summary>Total chunks produced per ingestion. Tags: rag.retrieval.strategy.</summary>
	public static Counter<long> ChunksProduced { get; } =
		AppInstrument.Meter.CreateCounter<long>(
			RagConventions.IngestChunksProduced, "{chunk}", "Chunks produced during ingestion");

	/// <summary>Total tokens sent to embedding model. Tags: rag.model.tier.</summary>
	public static Counter<long> TokensEmbedded { get; } =
		AppInstrument.Meter.CreateCounter<long>(
			RagConventions.IngestTokensEmbedded, "{token}", "Tokens sent to embedding model");

	/// <summary>Ingestion pipeline duration in milliseconds.</summary>
	public static Histogram<double> Duration { get; } =
		AppInstrument.Meter.CreateHistogram<double>(
			RagConventions.IngestionDuration, "{ms}", "Ingestion pipeline duration");

	/// <summary>Total documents ingested. Tags: rag.retrieval.strategy.</summary>
	public static Counter<long> Documents { get; } =
		AppInstrument.Meter.CreateCounter<long>(
			RagConventions.IngestionDocuments, "{document}", "Total documents ingested");

	/// <summary>Ingestion errors.</summary>
	public static Counter<long> Errors { get; } =
		AppInstrument.Meter.CreateCounter<long>(
			"rag.ingest.errors", "{error}", "Ingestion pipeline errors");
}
