using System.Text.Json;

namespace Presentation.AgentHub.DTOs;

/// <summary>Describes a single MCP tool available for invocation via the HTTP API.</summary>
public sealed record McpToolDto
{
    /// <summary>Unique tool name used as the <c>{name}</c> path segment in invoke requests.</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable description of what the tool does.</summary>
    public required string Description { get; init; }

    /// <summary>JSON Schema describing the tool's input parameters. Serialized as <c>inputSchema</c> (MCP spec).</summary>
    public JsonElement InputSchema { get; init; }
}
