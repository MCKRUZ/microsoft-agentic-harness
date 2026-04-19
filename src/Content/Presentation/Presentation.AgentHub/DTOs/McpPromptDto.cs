using Domain.AI.MCP;

namespace Presentation.AgentHub.DTOs;

/// <summary>Describes a single MCP prompt template.</summary>
public sealed record McpPromptDto
{
    /// <summary>Unique prompt name.</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable description of the prompt.</summary>
    public required string Description { get; init; }

    /// <summary>Structured argument descriptors accepted by the prompt. Serialized as <c>arguments</c> (MCP spec).</summary>
    public IReadOnlyList<McpPromptArgument> Arguments { get; init; } = [];
}
