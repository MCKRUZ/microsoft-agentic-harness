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

    // Required: a plan with no steps is meaningless, and GenerateAsync's own empty-plan check
    // (Steps.Count == 0) becomes redundant with the schema itself once this is required — a model
    // that omits "steps" now fails to parse rather than sailing through Deserialize as {} and
    // failing two statements later. See LlmStepOutput.Name/Type and LlmEdgeOutput.From/To for the
    // same reasoning applied one level down.
    [JsonPropertyName("steps")]
    public required IReadOnlyList<LlmStepOutput> Steps { get; init; }

    [JsonPropertyName("edges")]
    public IReadOnlyList<LlmEdgeOutput> Edges { get; init; } = [];

    [JsonPropertyName("configuration")]
    public LlmPlanConfigOutput? Configuration { get; init; }
}

internal sealed record LlmStepOutput
{
    // Required: LlmPlanOutputMapper.MapToPlanGraph indexes steps by Name (nameToId dictionary) and
    // ParseStepType throws InvalidOperationException on any Type it doesn't recognise, including
    // the empty string the old default silently produced. Both are already-mandatory-in-practice;
    // this makes the schema say so instead of letting a missing value surface as a mapper crash.
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("configuration")]
    public JsonElement Configuration { get; init; }

    [JsonPropertyName("retryPolicy")]
    public LlmRetryPolicyOutput? RetryPolicy { get; init; }

    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; init; } = 60;
}

internal sealed record LlmEdgeOutput
{
    // Required: LlmPlanOutputMapper.MapEdges throws InvalidOperationException if From/To don't
    // resolve against the step-name map — an edge with a missing endpoint was already unusable,
    // this just moves the failure to schema validation instead of a mapper-level exception.
    [JsonPropertyName("from")]
    public required string From { get; init; }

    [JsonPropertyName("to")]
    public required string To { get; init; }

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
