// src/Content/Infrastructure/Infrastructure.AI/Routing/TaskComplexityClassifier.cs
using System.Text.Json;
using Application.AI.Common.Interfaces.Routing;
using Domain.AI.Routing.Enums;
using Domain.AI.Routing.Models;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Routing;

/// <summary>
/// LLM-based few-shot complexity classifier. Used as fallback when the heuristic
/// is not confident. Routes through IModelRouter to use the economy-tier model.
/// </summary>
public sealed class TaskComplexityClassifier : ITaskComplexityClassifier
{
    private static readonly TaskComplexityAssessment FallbackAssessment = new()
    {
        Complexity = TaskComplexity.Moderate,
        Confidence = 0.5,
        Source = ClassificationSource.LlmClassifier,
        Reasoning = "Fallback — classification failed or was ambiguous"
    };

    private const string SystemPrompt = """
        You are a task complexity classifier. Given a user message and context, classify its complexity.

        ## Complexity Levels

        - **trivial**: Greetings, acknowledgments, simple yes/no answers, parametric knowledge lookups.
          Examples: "hi", "thanks", "what does DI stand for?"

        - **simple**: Single-step reasoning, one tool usage, straightforward Q&A.
          Examples: "show me the file structure", "what's in this config?", "list all endpoints"

        - **moderate**: Multi-step reasoning, multiple tools, synthesis across files, comparison.
          Examples: "compare these two implementations", "explain how requests flow through the pipeline"

        - **complex**: Deep reasoning, multi-hop analysis, code generation, refactoring, planning, architecture.
          Examples: "refactor the auth system", "design a caching layer", "plan the migration to microservices"

        ## Response Format

        Respond with ONLY a JSON object:
        {"complexity": "trivial|simple|moderate|complex", "confidence": 0.0-1.0, "reasoning": "brief explanation"}
        """;

    private readonly IModelRouter _modelRouter;
    private readonly ILogger<TaskComplexityClassifier> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="TaskComplexityClassifier"/>.
    /// </summary>
    /// <param name="modelRouter">Router used to obtain the economy-tier client for classification.</param>
    /// <param name="logger">Logger for fallback warnings.</param>
    public TaskComplexityClassifier(
        IModelRouter modelRouter,
        ILogger<TaskComplexityClassifier> logger)
    {
        _modelRouter = modelRouter;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task<TaskComplexityAssessment> ClassifyAsync(
        AgentTurnContext context,
        CancellationToken ct = default)
    {
        var userPrompt = $"""
            User message: "{context.UserMessage}"
            Turn number: {context.TurnNumber}
            Available tools: {context.AvailableToolCount}
            Recent tools used: {(context.RecentToolNames is { Count: > 0 } tools ? string.Join(", ", tools) : "none")}
            """;

        return LlmFewShotClassifier.ClassifyAsync(
            _modelRouter,
            "complexity_classification",
            SystemPrompt,
            userPrompt,
            ParseResponse,
            FallbackAssessment,
            _logger,
            $"conversation {context.ConversationId}",
            ct);
    }

    private static TaskComplexityAssessment ParseResponse(JsonElement root)
    {
        var complexityStr = root.GetProperty("complexity").GetString() ?? "moderate";
        var confidence = root.GetProperty("confidence").GetDouble();
        var reasoning = root.TryGetProperty("reasoning", out var reasonProp) ? reasonProp.GetString() : null;

        var complexity = complexityStr.ToLowerInvariant() switch
        {
            "trivial" => TaskComplexity.Trivial,
            "simple" => TaskComplexity.Simple,
            "moderate" => TaskComplexity.Moderate,
            "complex" => TaskComplexity.Complex,
            _ => TaskComplexity.Moderate
        };

        return new TaskComplexityAssessment
        {
            Complexity = complexity,
            Confidence = Math.Clamp(confidence, 0.0, 1.0),
            Source = ClassificationSource.LlmClassifier,
            Reasoning = reasoning
        };
    }
}
