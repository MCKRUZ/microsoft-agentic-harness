namespace Domain.AI.MCP;

/// <summary>
/// Describes a single argument accepted by an MCP prompt template.
/// Field names mirror the MCP specification and the WebUI Zod schema.
/// </summary>
/// <param name="Name">Argument name as referenced inside the prompt template.</param>
/// <param name="Description">Optional human-readable description of the argument.</param>
/// <param name="Required">Whether the argument must be supplied when invoking the prompt. Defaults to <see langword="false"/>.</param>
public sealed record McpPromptArgument(
    string Name,
    string? Description = null,
    bool? Required = null);
