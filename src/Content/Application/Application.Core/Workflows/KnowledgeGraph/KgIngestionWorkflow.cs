using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.RAG;
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
/// This workflow decomposes <see cref="IGraphRagService.IndexCorpusAsync"/> into discrete,
/// observable executor stages. Each stage is independently testable and emits its own
/// workflow events for observability.
/// </para>
/// <para>
/// The workflow uses <see cref="IRagModelRouter"/> (via <see cref="ExtractEntitiesExecutor"/>)
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
    /// <see cref="IRagModelRouter"/>, <see cref="IProvenanceStamper"/>,
    /// and <see cref="IKnowledgeGraphStore"/>.
    /// </param>
    /// <returns>A configured <see cref="Workflow"/> ready for execution via <see cref="InProcessExecution"/>.</returns>
    public static Workflow Build(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var extract = new ExtractEntitiesExecutor(
            services.GetRequiredService<IRagModelRouter>(),
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
