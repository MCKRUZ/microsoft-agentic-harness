namespace Presentation.AgentHub.DTOs;

/// <summary>Describes a single MCP prompt template.</summary>
public sealed record McpPromptDto
{
    /// <summary>Unique prompt name.</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable description of the prompt.</summary>
    public required string Description { get; init; }

    /// <summary>Names of the arguments the prompt accepts.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];
}
