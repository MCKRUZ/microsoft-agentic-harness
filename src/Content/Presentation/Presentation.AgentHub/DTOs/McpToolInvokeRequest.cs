using System.Text.Json;

namespace Presentation.AgentHub.DTOs;

/// <summary>Request body for invoking an MCP tool directly via HTTP.</summary>
public sealed record McpToolInvokeRequest
{
    /// <summary>
    /// Tool arguments as a JSON object. Passed verbatim to the underlying
    /// <c>AIFunction.InvokeAsync</c>. Each property maps to a named parameter.
    /// </summary>
    public JsonElement Arguments { get; init; }
}
