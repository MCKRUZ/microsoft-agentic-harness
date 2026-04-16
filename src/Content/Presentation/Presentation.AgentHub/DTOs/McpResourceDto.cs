namespace Presentation.AgentHub.DTOs;

/// <summary>Describes a single MCP resource exposed by the server.</summary>
public sealed record McpResourceDto
{
    /// <summary>Resource URI (e.g. <c>trace://run-id/</c>).</summary>
    public required string Uri { get; init; }

    /// <summary>Human-readable resource name.</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable description of the resource.</summary>
    public required string Description { get; init; }

    /// <summary>Optional MIME type of the resource content.</summary>
    public string? MimeType { get; init; }
}
