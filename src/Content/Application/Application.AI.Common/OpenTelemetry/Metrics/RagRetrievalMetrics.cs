using System.Diagnostics.Metrics;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Telemetry;

namespace Application.AI.Common.OpenTelemetry.Metrics;

/// <summary>
/// OTel metric instruments for RAG retrieval and reranking operations.
/// Tracks retrieval latency, chunk counts, rerank duration, and errors.
/// </summary>
public static class RagRetrievalMetrics
{
    /// <summary>Retrieval pipeline duration in milliseconds (vector search + BM25 + fusion).</summary>
    public static Histogram<double> RetrievalDuration { get; } =
        AppInstrument.Meter.CreateHistogram<double>(
            RagConventions.RetrievalDuration, "{ms}", "Retrieval pipeline duration");

    /// <summary>Number of chunks returned from retrieval before reranking.</summary>
    public static Histogram<int> ChunksReturned { get; } =
        AppInstrument.Meter.CreateHistogram<int>(
            RagConventions.RetrievalChunksReturned, "{chunk}", "Chunks returned from retrieval");

    /// <summary>Reranking pass duration in milliseconds.</summary>
    public static Histogram<double> RerankDuration { get; } =
        AppInstrument.Meter.CreateHistogram<double>(
            RagConventions.RerankDuration, "{ms}", "Reranking duration");

    /// <summary>Total retrieval pipeline errors.</summary>
    public static Counter<long> Errors { get; } =
        AppInstrument.Meter.CreateCounter<long>(
            "rag.retrieval.errors", "{error}", "Retrieval pipeline errors");
}
