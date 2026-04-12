using System.Text.Json.Serialization;

namespace Domain.Common.MetaHarness;

/// <summary>
/// Represents one JSONL line in <c>traces.jsonl</c>.
/// Uses <see cref="JsonPropertyNameAttribute"/> attributes for snake_case serialization.
/// </summary>
public sealed record ExecutionTraceRecord
{
    [JsonPropertyName("seq")]
    public long Seq { get; init; }

    [JsonPropertyName("ts")]
    public DateTimeOffset Ts { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("execution_run_id")]
    public string ExecutionRunId { get; init; } = string.Empty;

    [JsonPropertyName("candidate_id")]
    public string? CandidateId { get; init; }

    [JsonPropertyName("iteration")]
    public int? Iteration { get; init; }

    [JsonPropertyName("task_id")]
    public string? TaskId { get; init; }

    [JsonPropertyName("turn_id")]
    public string TurnId { get; init; } = string.Empty;

    [JsonPropertyName("tool_name")]
    public string? ToolName { get; init; }

    [JsonPropertyName("result_category")]
    public string? ResultCategory { get; init; }

    [JsonPropertyName("payload_summary")]
    public string? PayloadSummary { get; init; }

    [JsonPropertyName("payload_full_path")]
    public string? PayloadFullPath { get; init; }

    [JsonPropertyName("redacted")]
    public bool? Redacted { get; init; }
}

/// <summary>Valid values for <see cref="ExecutionTraceRecord.Type"/>.</summary>
public static class TraceRecordTypes
{
    public const string ToolCall = "tool_call";
    public const string ToolResult = "tool_result";
    public const string Decision = "decision";
    public const string Observation = "observation";
}

/// <summary>Valid values for <see cref="ExecutionTraceRecord.ResultCategory"/>.</summary>
public static class TraceResultCategories
{
    public const string Success = "success";
    public const string Partial = "partial";
    public const string Error = "error";
    public const string Timeout = "timeout";
    public const string Blocked = "blocked";
}
