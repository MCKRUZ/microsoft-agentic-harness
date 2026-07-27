using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.RAG;
using Application.AI.Common.Interfaces.Routing;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Application.Core.Workflows.KnowledgeGraph;

/// <summary>
/// Static factory that builds the knowledge graph ingestion pipeline as a MAF
/// <see cref="Workflow"/> graph. The graph structure is a simple sequential chain:
/// <code>
///   ExtractEntities → StampProvenance → StoreGraph → [Output]
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// This is the build path for the <em>knowledge graph</em> — the tenant-aware
/// <see cref="IKnowledgeGraphStore"/> shared with memory and learnings — as opposed to the
/// corpus graph that <see cref="IGraphRagService.IndexCorpusAsync"/> builds for the GraphRag
/// retrieval strategy. The two graphs are physically separate stores; this workflow mirrors
/// the extract → stamp → store shape as discrete, observable executor stages, each
/// independently testable and emitting its own workflow events.
/// </para>
/// <para>
/// <b>Consumer:</b> the knowledge-graph enrichment stage of
/// <c>IngestDocumentCommandHandler</c>, which resolves this workflow by its DI key
/// (<c>"kg-ingestion"</c>) and runs it over the ingested chunks when
/// <c>AppConfig:AI:Rag:GraphRag:EnrichKnowledgeGraphOnIngest</c> is <c>true</c>. Because
/// <see cref="StoreGraphExecutor"/> writes through the root
/// <see cref="IKnowledgeGraphStore"/> registration, extracted entities pass through the
/// tenant-isolation and compliance decorator chain and are stamped accordingly.
/// </para>
/// <para>
/// The workflow uses <see cref="IModelRouter"/> (via <see cref="ExtractEntitiesExecutor"/>)
/// to route entity extraction to the economy-tier LLM model, keeping ingestion costs low
/// for large document batches.
/// </para>
/// </remarks>
public static class KgIngestionWorkflow
{
    /// <summary>
    /// Builds the knowledge graph ingestion workflow from DI-resolved services.
    /// </summary>
    /// <param name="services">
    /// The service provider used to resolve pipeline dependencies:
    /// <see cref="IModelRouter"/>, <see cref="IProvenanceStamper"/>,
    /// and <see cref="IKnowledgeGraphStore"/>.
    /// </param>
    /// <returns>A configured <see cref="Workflow"/> ready for execution via <see cref="InProcessExecution"/>.</returns>
    public static Workflow Build(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var extract = new ExtractEntitiesExecutor(
            services.GetRequiredService<IModelRouter>(),
            services.GetRequiredService<ILogger<ExtractEntitiesExecutor>>());

        var stamp = new StampProvenanceExecutor(
            services.GetRequiredService<IProvenanceStamper>(),
            services.GetRequiredService<ILogger<StampProvenanceExecutor>>());

        var store = new StoreGraphExecutor(
            services.GetRequiredService<IKnowledgeGraphStore>(),
            services.GetRequiredService<ILogger<StoreGraphExecutor>>());

        var builder = new WorkflowBuilder(extract);
        builder.AddEdge(extract, stamp);
        builder.AddEdge(stamp, store);
        builder.WithOutputFrom(store);

        return builder.Build();
    }
}
