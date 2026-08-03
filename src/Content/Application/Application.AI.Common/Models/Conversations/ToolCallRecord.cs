using System.Text.Json;

namespace Application.AI.Common.Models.Conversations;

/// <summary>Captures a single tool invocation within an assistant turn.</summary>
public sealed record ToolCallRecord(
    string ToolName,
    JsonElement Input,
    JsonElement Output,
    long DurationMs);
