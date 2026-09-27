// src/Content/Application/Application.AI.Common/Interfaces/Routing/IRequestIntentClassifier.cs
using Domain.AI.Routing.Models;

namespace Application.AI.Common.Interfaces.Routing;

/// <summary>
/// LLM-based few-shot classifier for what kind of request a user message represents — sibling
/// to <see cref="ITaskComplexityClassifier"/>, which classifies how hard the request is instead
/// of what kind it is. Uses the economy-tier model for classification.
/// </summary>
public interface IRequestIntentClassifier
{
    /// <summary>
    /// Classifies the request's intent using an LLM with few-shot examples.
    /// Adds ~200ms latency. Falls back to <c>RequestIntent.Other</c> on any failure.
    /// </summary>
    Task<RequestIntentAssessment> ClassifyAsync(
        AgentTurnContext context,
        CancellationToken ct = default);
}
