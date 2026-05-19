using System.Text.Json.Serialization;

namespace Presentation.AgentHub.AgUi;

/// <summary>
/// Signals that a plan has started executing.
/// </summary>
public sealed record PlanStartedEvent : AgUiEvent
{
    /// <summary>Identifier of the plan.</summary>
    [JsonPropertyName("planId")]
    public required string PlanId { get; init; }

    /// <summary>Human-readable plan name.</summary>
    [JsonPropertyName("planName")]
    public required string PlanName { get; init; }

    /// <summary>Total number of steps in the plan graph.</summary>
    [JsonPropertyName("totalSteps")]
    public required int TotalSteps { get; init; }
}

/// <summary>
/// Signals that a plan step has started executing.
/// </summary>
public sealed record PlanStepStartedEvent : AgUiEvent
{
    /// <summary>Identifier of the plan.</summary>
    [JsonPropertyName("planId")]
    public required string PlanId { get; init; }

    /// <summary>Identifier of the step.</summary>
    [JsonPropertyName("stepId")]
    public required string StepId { get; init; }

    /// <summary>Human-readable step name.</summary>
    [JsonPropertyName("stepName")]
    public required string StepName { get; init; }

    /// <summary>The step's execution type (e.g. "LlmCall", "ToolUse").</summary>
    [JsonPropertyName("stepType")]
    public required string StepType { get; init; }
}

/// <summary>
/// Signals that a plan step has completed (successfully, failed, or skipped).
/// </summary>
public sealed record PlanStepCompletedEvent : AgUiEvent
{
    /// <summary>Identifier of the plan.</summary>
    [JsonPropertyName("planId")]
    public required string PlanId { get; init; }

    /// <summary>Identifier of the step.</summary>
    [JsonPropertyName("stepId")]
    public required string StepId { get; init; }

    /// <summary>Final status of the step (e.g. "Completed", "Failed").</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>Wall-clock duration in milliseconds.</summary>
    [JsonPropertyName("durationMs")]
    public required long DurationMs { get; init; }

    /// <summary>Brief summary of step output. Null if no output.</summary>
    [JsonPropertyName("outputSummary")]
    public string? OutputSummary { get; init; }
}

/// <summary>
/// An incremental JSON-Patch delta applied to plan step state.
/// Uses RFC 6902 patch operations to encode step status transitions.
/// </summary>
public sealed record PlanStateUpdateEvent : AgUiEvent
{
    /// <summary>Identifier of the plan.</summary>
    [JsonPropertyName("planId")]
    public required string PlanId { get; init; }

    /// <summary>RFC 6902 JSON Patch operations.</summary>
    [JsonPropertyName("patch")]
    public required IReadOnlyList<JsonPatchOperation> Patch { get; init; }
}

/// <summary>
/// A single RFC 6902 JSON Patch operation.
/// </summary>
public sealed record JsonPatchOperation
{
    /// <summary>Patch operation type (e.g. "replace", "add", "remove").</summary>
    [JsonPropertyName("op")]
    public required string Op { get; init; }

    /// <summary>JSON Pointer path to the target value.</summary>
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    /// <summary>The value to apply at the target path.</summary>
    [JsonPropertyName("value")]
    public required object Value { get; init; }
}

/// <summary>
/// Reports sandbox resource usage and attestation for a tool execution step.
/// </summary>
public sealed record SandboxStatusEvent : AgUiEvent
{
    /// <summary>Identifier of the plan.</summary>
    [JsonPropertyName("planId")]
    public required string PlanId { get; init; }

    /// <summary>Identifier of the step.</summary>
    [JsonPropertyName("stepId")]
    public required string StepId { get; init; }

    /// <summary>Name of the tool being executed.</summary>
    [JsonPropertyName("toolName")]
    public required string ToolName { get; init; }

    /// <summary>Sandbox isolation level (e.g. "None", "Process", "Container").</summary>
    [JsonPropertyName("isolationLevel")]
    public required string IsolationLevel { get; init; }

    /// <summary>Memory consumed in bytes.</summary>
    [JsonPropertyName("memoryUsedBytes")]
    public required long MemoryUsedBytes { get; init; }

    /// <summary>CPU time consumed in milliseconds.</summary>
    [JsonPropertyName("cpuTimeMs")]
    public required long CpuTimeMs { get; init; }

    /// <summary>HMAC attestation hash. Null if attestation unavailable.</summary>
    [JsonPropertyName("attestationHash")]
    public string? AttestationHash { get; init; }
}

/// <summary>
/// Signals that an entire plan completed successfully.
/// </summary>
public sealed record PlanCompletedEvent : AgUiEvent
{
    /// <summary>Identifier of the plan.</summary>
    [JsonPropertyName("planId")]
    public required string PlanId { get; init; }

    /// <summary>Total wall-clock duration in milliseconds.</summary>
    [JsonPropertyName("totalDurationMs")]
    public required long TotalDurationMs { get; init; }
}

/// <summary>
/// Signals that a plan failed due to a step failure.
/// </summary>
public sealed record PlanFailedEvent : AgUiEvent
{
    /// <summary>Identifier of the plan.</summary>
    [JsonPropertyName("planId")]
    public required string PlanId { get; init; }

    /// <summary>Identifier of the step that caused the failure.</summary>
    [JsonPropertyName("failedStepId")]
    public required string FailedStepId { get; init; }

    /// <summary>Error message from the failed step.</summary>
    [JsonPropertyName("errorMessage")]
    public required string ErrorMessage { get; init; }
}
