using System.Text.Json;

namespace Presentation.AgentHub.DTOs;

/// <summary>Response envelope for an MCP tool invocation.</summary>
public sealed record McpToolInvokeResponse
{
    /// <summary>Serialized output from the tool. Populated only when <see cref="Success"/> is <see langword="true"/>; <see langword="null"/> on failure.</summary>
    public JsonElement? Output { get; init; }

    /// <summary>Wall-clock duration of the invocation in milliseconds.</summary>
    public long DurationMs { get; init; }

    /// <summary><see langword="true"/> when the tool completed without throwing.</summary>
    public bool Success { get; init; }

    /// <summary>Sanitized error message. Populated only when <see cref="Success"/> is <see langword="false"/>.</summary>
    public string? Error { get; init; }
}
