using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Config;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.KnowledgeGraph.Provenance;

/// <summary>
/// Default <see cref="IProvenanceStamper"/> implementation that creates
/// <see cref="ProvenanceStamp"/> instances with the current UTC timestamp
/// and applies them to graph entities via record with-expressions.
/// </summary>
/// <remarks>
/// When <c>GraphRagConfig.ProvenanceEnabled</c> is <c>false</c>, stamping methods
/// return entities unchanged — no stamp is attached. This allows the pipeline to
/// call stamping unconditionally without branching on config.
/// </remarks>
public sealed class DefaultProvenanceStamper : IProvenanceStamper
{
    private readonly IOptionsMonitor<AppConfig> _configMonitor;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultProvenanceStamper"/> class.
    /// </summary>
    /// <param name="configMonitor">Application configuration for provenance settings.</param>
    /// <param name="timeProvider">Time provider for deterministic timestamps in tests.</param>
    public DefaultProvenanceStamper(
        IOptionsMonitor<AppConfig> configMonitor,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(configMonitor);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _configMonitor = configMonitor;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public ProvenanceStamp CreateStamp(
        string sourcePipeline,
        string sourceTask,
        string? sourceDocumentId = null,
        double? extractionConfidence = null,
        string? modifiedBy = null)
    {
        return new ProvenanceStamp
        {
            SourcePipeline = sourcePipeline,
            SourceTask = sourceTask,
            Timestamp = _timeProvider.GetUtcNow(),
            SourceDocumentId = sourceDocumentId,
            ExtractionConfidence = extractionConfidence,
            LastModifiedBy = modifiedBy
        };
    }

    /// <inheritdoc />
    public GraphNode StampNode(GraphNode node, ProvenanceStamp stamp)
    {
        if (!_configMonitor.CurrentValue.AI.Rag.GraphRag.ProvenanceEnabled)
            return node;

        return node with { Provenance = stamp };
    }

    /// <inheritdoc />
    public GraphEdge StampEdge(GraphEdge edge, ProvenanceStamp stamp)
    {
        if (!_configMonitor.CurrentValue.AI.Rag.GraphRag.ProvenanceEnabled)
            return edge;

        return edge with { Provenance = stamp };
    }
}
