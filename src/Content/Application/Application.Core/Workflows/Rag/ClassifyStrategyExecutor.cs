using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Enums;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;

namespace Application.Core.Workflows.Rag;

/// <summary>
/// Classifies the incoming query to determine the optimal retrieval strategy.
/// Wraps <see cref="IQueryClassifier"/> as a MAF workflow executor so the strategy
/// decision becomes a discrete, observable node in the RAG workflow graph.
/// </summary>
/// <remarks>
/// <para>
/// On classification failure, falls back to <see cref="RetrievalStrategy.HybridVectorBm25"/>
/// to ensure the pipeline degrades gracefully rather than failing the entire search.
/// </para>
/// <para>
/// When <see cref="RagWorkflowInput.StrategyOverride"/> is set, the classifier is bypassed
/// entirely and the override strategy is used. This enables deterministic testing and
/// specialized workflows that already know the optimal strategy.
/// </para>
/// </remarks>
public sealed class ClassifyStrategyExecutor(
    IQueryClassifier queryClassifier,
    ILogger<ClassifyStrategyExecutor> logger)
    : Executor<RagWorkflowInput, ClassifiedQuery>("classify_strategy")
{
    /// <summary>
    /// Classifies the query and produces a <see cref="ClassifiedQuery"/> with the selected strategy.
    /// </summary>
    /// <param name="message">The RAG workflow input containing the user query and parameters.</param>
    /// <param name="context">The workflow execution context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The classified query with the resolved retrieval strategy.</returns>
    public override async ValueTask<ClassifiedQuery> HandleAsync(
        RagWorkflowInput message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        if (message.StrategyOverride is not null
            && Enum.TryParse<RetrievalStrategy>(message.StrategyOverride, ignoreCase: true, out var overrideStrategy))
        {
            logger.LogInformation(
                "Strategy override applied: {Strategy}", overrideStrategy);

            return new ClassifiedQuery(
                message.Query, message.TopK, message.CollectionName, overrideStrategy);
        }

        try
        {
            var classification = await queryClassifier.ClassifyAsync(
                message.Query, cancellationToken);

            logger.LogInformation(
                "Query classified: Strategy={Strategy}, Confidence={Confidence:F2}",
                classification.Strategy, classification.Confidence);

            return new ClassifiedQuery(
                message.Query, message.TopK, message.CollectionName, classification.Strategy);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Query classification failed; falling back to HybridVectorBm25");

            return new ClassifiedQuery(
                message.Query, message.TopK, message.CollectionName,
                RetrievalStrategy.HybridVectorBm25);
        }
    }
}
