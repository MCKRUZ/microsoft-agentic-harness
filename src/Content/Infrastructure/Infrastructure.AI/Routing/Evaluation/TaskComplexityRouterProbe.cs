using System.Globalization;
using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Application.AI.Common.Interfaces.Routing;
using Domain.AI.Routing.Models;

namespace Infrastructure.AI.Routing.Evaluation;

/// <summary>
/// Eval probe that measures the agent task-complexity router. Wraps the existing
/// <see cref="ITaskComplexityClassifier"/> and exposes its decision as a normalized
/// <see cref="RouterDecision"/> for the <c>routing_accuracy</c> metric.
/// </summary>
/// <remarks>
/// <para>
/// The classifier consumes an <see cref="AgentTurnContext"/> rather than a bare string, so the
/// probe synthesizes a minimal single-turn context from the case input: turn 1, the input as the
/// user message, and an available-tool count read from the optional <c>tool_count</c> parameter
/// (default 0). This is a faithful approximation for a classification scorecard; multi-turn
/// escalation signals are intentionally out of scope.
/// </para>
/// <para>
/// The primary label is the <c>TaskComplexity</c> member name. Labeled cases target this probe with
/// <c>target: "router:task_complexity"</c> and an <c>expected_output</c> of the complexity name
/// (e.g. <c>Complex</c>).
/// </para>
/// </remarks>
public sealed class TaskComplexityRouterProbe : IRouterEvalProbe
{
    private const string ToolCountParameter = "tool_count";

    private readonly ITaskComplexityClassifier _classifier;

    /// <summary>
    /// Initializes a new instance of the <see cref="TaskComplexityRouterProbe"/> class.
    /// </summary>
    /// <param name="classifier">The production task-complexity classifier under measurement.</param>
    public TaskComplexityRouterProbe(ITaskComplexityClassifier classifier)
    {
        ArgumentNullException.ThrowIfNull(classifier);
        _classifier = classifier;
    }

    /// <inheritdoc />
    public string Key => "task_complexity";

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
            ConversationDepth = 1,
            AvailableToolCount = ResolveToolCount(parameters)
        };

        var assessment = await _classifier.ClassifyAsync(context, cancellationToken).ConfigureAwait(false);

        return new RouterDecision
        {
            Label = assessment.Complexity.ToString(),
            Confidence = assessment.Confidence,
            Reasoning = assessment.Reasoning is { Length: > 0 } r
                ? $"[{assessment.Source}] {r}"
                : assessment.Source.ToString()
        };
    }

    private static int ResolveToolCount(IReadOnlyDictionary<string, string> parameters) =>
        parameters.TryGetValue(ToolCountParameter, out var raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
            && count >= 0
                ? count
                : 0;
}
