using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Application.AI.Common.Interfaces.Routing;
using Domain.AI.Routing.Models;

namespace Infrastructure.AI.Routing.Evaluation;

/// <summary>
/// Eval probe that measures the request-intent classifier. Wraps
/// <see cref="IRequestIntentClassifier"/> and exposes its decision as a normalized
/// <see cref="RouterDecision"/> for the <c>routing_accuracy</c> metric — sibling to
/// <see cref="TaskComplexityRouterProbe"/>.
/// </summary>
/// <remarks>
/// The classifier consumes an <see cref="AgentTurnContext"/> rather than a bare string, so the
/// probe synthesizes a minimal single-turn context from the case input (turn 1, the input as the
/// user message; the classifier doesn't read tool count or history, so nothing else is needed).
/// The primary label is the <see cref="Domain.AI.Routing.Enums.RequestIntent"/> member name.
/// Labeled cases target this probe with <c>target: "router:request_intent"</c> and an
/// <c>expected_output</c> of the intent name (e.g. <c>CodeGeneration</c>).
/// </remarks>
public sealed class RequestIntentRouterProbe : IRouterEvalProbe
{
    private readonly IRequestIntentClassifier _classifier;

    /// <summary>
    /// Initializes a new instance of the <see cref="RequestIntentRouterProbe"/> class.
    /// </summary>
    /// <param name="classifier">The production request-intent classifier under measurement.</param>
    public RequestIntentRouterProbe(IRequestIntentClassifier classifier)
    {
        ArgumentNullException.ThrowIfNull(classifier);
        _classifier = classifier;
    }

    /// <inheritdoc />
    public string Key => "request_intent";

    /// <inheritdoc />
    public async Task<RouterDecision> ClassifyAsync(
        string input,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(parameters);

        var context = new AgentTurnContext
        {
            ConversationId = "router-eval",
            UserMessage = input,
            TurnNumber = 1,
            ConversationDepth = 1
        };

        var assessment = await _classifier.ClassifyAsync(context, cancellationToken).ConfigureAwait(false);

        return new RouterDecision
        {
            Label = assessment.Intent.ToString(),
            Confidence = assessment.Confidence,
            Reasoning = assessment.Reasoning is { Length: > 0 } r
                ? $"[{assessment.Source}] {r}"
                : assessment.Source.ToString()
        };
    }
}
