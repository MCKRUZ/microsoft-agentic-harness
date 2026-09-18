// src/Content/Infrastructure/Infrastructure.AI/Routing/RequestIntentClassifier.cs
using System.Text.Json;
using Application.AI.Common.Interfaces.Routing;
using Domain.AI.Routing.Enums;
using Domain.AI.Routing.Models;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Routing;

/// <summary>
/// LLM-based few-shot request-intent classifier. Routes through IModelRouter to use the
/// economy-tier model — same shape as <see cref="TaskComplexityClassifier"/>, classifying a
/// different dimension.
/// </summary>
public sealed class RequestIntentClassifier : IRequestIntentClassifier
{
    /// <summary>
    /// Confidence stamped on a classification that failed and fell back, rather than one the model
    /// was actually unsure about. A consumer that gates on confidence — <see cref="AgentRouter"/> is
    /// the one today — must use a threshold strictly above this value, or a failure silently reads as
    /// "confident enough," which is the opposite of what the fallback exists to signal. Exposed
    /// (rather than left as a literal on <see cref="FallbackAssessment"/>) specifically so that
    /// threshold is a compile-time reference to this value, not an independently-tuned number that
    /// can drift out of sync with it.
    /// </summary>
    internal const double FallbackConfidence = 0.5;

    private static readonly RequestIntentAssessment FallbackAssessment = new()
    {
        Intent = RequestIntent.Other,
        Confidence = FallbackConfidence,
        Source = ClassificationSource.LlmClassifier,
        Reasoning = "Fallback — classification failed or was ambiguous"
    };

    private const string SystemPrompt = """
        You are a request-intent classifier. Given a user message, classify what kind of
        request it is.

        ## Intents

        - **question**: A factual question or lookup, expecting an answer.
          Examples: "what does DI stand for?", "how does the retry policy work?"

        - **task_execution**: A request to perform a concrete action or produce a deliverable.
          Examples: "deploy the latest build", "generate a report for last month"

        - **code_generation**: A request to write, modify, or review source code.
          Examples: "add a null check here", "refactor this class", "review this PR"

        - **creative_content**: A request for original written or visual content.
          Examples: "write a product announcement", "draft a landing page headline"

        - **research**: A request to investigate, gather information, or synthesize findings.
          Examples: "compare these two vendors", "find prior art for this approach"

        - **planning**: A request to design an approach, roadmap, or architecture before acting.
          Examples: "plan the migration", "design the caching layer"

        - **conversational**: Small talk, greetings, or acknowledgments with no task content.
          Examples: "hi", "thanks", "sounds good"

        - **other**: Doesn't fit any of the above, or intent is unclear.

        ## Response Format

        Respond with ONLY a JSON object:
        {"intent": "question|task_execution|code_generation|creative_content|research|planning|conversational|other", "confidence": 0.0-1.0, "reasoning": "brief explanation"}
        """;

    private readonly IModelRouter _modelRouter;
    private readonly ILogger<RequestIntentClassifier> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="RequestIntentClassifier"/>.
    /// </summary>
    /// <param name="modelRouter">Router used to obtain the economy-tier client for classification.</param>
    /// <param name="logger">Logger for fallback warnings.</param>
    public RequestIntentClassifier(
        IModelRouter modelRouter,
        ILogger<RequestIntentClassifier> logger)
    {
        _modelRouter = modelRouter;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task<RequestIntentAssessment> ClassifyAsync(
        AgentTurnContext context,
        CancellationToken ct = default)
    {
        var userPrompt = $"""
            User message: "{context.UserMessage}"
            """;

        return LlmFewShotClassifier.ClassifyAsync(
            _modelRouter,
            "intent_classification",
            SystemPrompt,
            userPrompt,
            ParseResponse,
            FallbackAssessment,
            _logger,
            $"conversation {context.ConversationId}",
            ct);
    }

    private static RequestIntentAssessment ParseResponse(JsonElement root)
    {
        var intentStr = root.GetProperty("intent").GetString() ?? "other";
        var confidence = root.GetProperty("confidence").GetDouble();
        var reasoning = root.TryGetProperty("reasoning", out var reasonProp) ? reasonProp.GetString() : null;

        var intent = intentStr.ToLowerInvariant() switch
        {
            "question" => RequestIntent.Question,
            "task_execution" => RequestIntent.TaskExecution,
            "code_generation" => RequestIntent.CodeGeneration,
            "creative_content" => RequestIntent.CreativeContent,
            "research" => RequestIntent.Research,
            "planning" => RequestIntent.Planning,
            "conversational" => RequestIntent.Conversational,
            _ => RequestIntent.Other
        };

        return new RequestIntentAssessment
        {
            Intent = intent,
            Confidence = Math.Clamp(confidence, 0.0, 1.0),
            Source = ClassificationSource.LlmClassifier,
            Reasoning = reasoning
        };
    }
}
