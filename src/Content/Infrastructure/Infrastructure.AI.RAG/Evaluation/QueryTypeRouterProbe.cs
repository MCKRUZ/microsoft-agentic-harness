using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Application.AI.Common.Interfaces.RAG;

namespace Infrastructure.AI.RAG.Evaluation;

/// <summary>
/// Eval probe that measures the RAG query-type router. Wraps the existing
/// <see cref="IQueryClassifier"/> and exposes its decision as a normalized
/// <see cref="RouterDecision"/> for the <c>routing_accuracy</c> metric.
/// </summary>
/// <remarks>
/// The probe adds no behavior of its own — it calls the production classifier and maps its
/// <c>QueryClassification</c> onto the transport-agnostic decision shape. The primary label is the
/// <c>QueryType</c> member name; the selected <c>RetrievalStrategy</c> rides along as the secondary
/// label for reporting. Labeled cases target this probe with <c>target: "router:query_type"</c> and
/// an <c>expected_output</c> of the query-type name (e.g. <c>MultiHop</c>).
/// </remarks>
public sealed class QueryTypeRouterProbe : IRouterEvalProbe
{
    private readonly IQueryClassifier _classifier;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryTypeRouterProbe"/> class.
    /// </summary>
    /// <param name="classifier">The production query-type classifier under measurement.</param>
    public QueryTypeRouterProbe(IQueryClassifier classifier)
    {
        ArgumentNullException.ThrowIfNull(classifier);
        _classifier = classifier;
    }

    /// <inheritdoc />
    public string Key => "query_type";

    /// <inheritdoc />
    public async Task<RouterDecision> ClassifyAsync(
        string input,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var classification = await _classifier.ClassifyAsync(input, cancellationToken).ConfigureAwait(false);

        return new RouterDecision
        {
            Label = classification.Type.ToString(),
            SecondaryLabel = classification.Strategy.ToString(),
            Confidence = classification.Confidence,
            Reasoning = classification.Reasoning
        };
    }
}
