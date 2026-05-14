using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Enums;
using Domain.AI.RAG.Models;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Application.Core.Workflows.Rag;

/// <summary>
/// Static factory that builds the RAG pipeline as a MAF <see cref="Workflow"/> graph.
/// The graph structure is:
/// <code>
///   ClassifyStrategy
///        |
///   [Switch on Strategy]
///        |                    \
///   VectorRetrieval       GraphRagSearch
///        |                    |
///   AssembleContext       (outputs directly)
///        |
///   [Output]              [Output]
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// The CRAG refinement loop (retrieve-rerank-evaluate, retry on Refine) is encapsulated
/// inside <see cref="VectorRetrievalExecutor"/> because DAGs cannot represent cycles.
/// This keeps the workflow graph clean while preserving the refinement behavior.
/// </para>
/// <para>
/// Both the vector path (via <see cref="AssembleContextExecutor"/>) and the graph path
/// (via <see cref="GraphRagSearchExecutor"/>) produce <see cref="RagAssembledContext"/>,
/// so consumers receive a uniform output type regardless of the retrieval strategy.
/// </para>
/// </remarks>
public static class RagPipelineWorkflow
{
    /// <summary>
    /// Builds the RAG pipeline workflow graph from DI-resolved services.
    /// </summary>
    /// <param name="services">
    /// The service provider used to resolve pipeline dependencies:
    /// <see cref="IQueryClassifier"/>, <see cref="IHybridRetriever"/>,
    /// <see cref="IReranker"/>, <see cref="ICragEvaluator"/>,
    /// <see cref="IRagContextAssembler"/>, <see cref="IGraphRagService"/>,
    /// and optionally <see cref="IFeedbackWeightedScorer"/>.
    /// </param>
    /// <returns>A configured <see cref="Workflow"/> ready for execution via <see cref="InProcessExecution"/>.</returns>
    public static Workflow Build(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var classify = new ClassifyStrategyExecutor(
            services.GetRequiredService<IQueryClassifier>(),
            services.GetRequiredService<ILogger<ClassifyStrategyExecutor>>());

        var vectorRetrieval = new VectorRetrievalExecutor(
            services.GetRequiredService<IHybridRetriever>(),
            services.GetRequiredService<IReranker>(),
            services.GetRequiredService<ICragEvaluator>(),
            services.GetService<IFeedbackWeightedScorer>(),
            services.GetRequiredService<ILogger<VectorRetrievalExecutor>>());

        var assembleContext = new AssembleContextExecutor(
            services.GetRequiredService<IRagContextAssembler>(),
            services.GetRequiredService<ILogger<AssembleContextExecutor>>());

        var graphSearch = new GraphRagSearchExecutor(
            services.GetRequiredService<IGraphRagService>(),
            services.GetRequiredService<ILogger<GraphRagSearchExecutor>>());

        var builder = new WorkflowBuilder(classify);

        builder.AddSwitch(classify, switchBuilder => switchBuilder
            .AddCase<ClassifiedQuery>(
                q => q is not null && q.Strategy == RetrievalStrategy.GraphRag,
                [graphSearch])
            .WithDefault([vectorRetrieval]));

        builder.AddEdge(vectorRetrieval, assembleContext);

        builder.WithOutputFrom(assembleContext, graphSearch);

        return builder.Build();
    }
}
