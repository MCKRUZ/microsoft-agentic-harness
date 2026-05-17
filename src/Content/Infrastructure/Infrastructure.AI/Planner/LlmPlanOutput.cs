using System.Text.Json;
using System.Text.Json.Serialization;

namespace Infrastructure.AI.Planner;

/// <summary>
/// Intermediate DTO for deserializing raw LLM JSON output before mapping to domain types.
/// LLMs produce human-readable step names in edges rather than GUIDs, so this model
/// uses string names that get resolved to <c>PlanStepId</c> values during post-processing.
/// </summary>
internal sealed record LlmPlanOutput
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("steps")]
    public IReadOnlyList<LlmStepOutput> Steps { get; init; } = [];

    [JsonPropertyName("edges")]
    public IReadOnlyList<LlmEdgeOutput> Edges { get; init; } = [];

    [JsonPropertyName("configuration")]
    public LlmPlanConfigOutput? Configuration { get; init; }
}

internal sealed record LlmStepOutput
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("configuration")]
    public JsonElement Configuration { get; init; }

    [JsonPropertyName("retryPolicy")]
    public LlmRetryPolicyOutput? RetryPolicy { get; init; }

    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; init; } = 60;
}

internal sealed record LlmEdgeOutput
{
    [JsonPropertyName("from")]
    public string From { get; init; } = "";

    [JsonPropertyName("to")]
    public string To { get; init; } = "";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "ControlFlow";

    [JsonPropertyName("condition")]
    public string? Condition { get; init; }
}

internal sealed record LlmRetryPolicyOutput
{
    [JsonPropertyName("maxRetries")]
    public int MaxRetries { get; init; } = 3;

    [JsonPropertyName("initialDelayMs")]
    public int InitialDelayMs { get; init; } = 1000;

    [JsonPropertyName("strategy")]
    public string Strategy { get; init; } = "Exponential";

    [JsonPropertyName("onExhausted")]
    public string OnExhausted { get; init; } = "FailStep";
}

internal sealed record LlmPlanConfigOutput
{
    [JsonPropertyName("planTimeoutMinutes")]
    public int PlanTimeoutMinutes { get; init; } = 30;

    [JsonPropertyName("maxParallelSteps")]
    public int MaxParallelSteps { get; init; } = 10;

    [JsonPropertyName("maxSubPlanDepth")]
    public int MaxSubPlanDepth { get; init; } = 5;
}
