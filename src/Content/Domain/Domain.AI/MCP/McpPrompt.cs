namespace Domain.AI.MCP;

/// <summary>
/// Describes a prompt template exposed by an MCP prompt provider.
/// Returned by <c>IMcpPromptProvider.GetPromptsAsync</c>.
/// </summary>
/// <param name="Name">Unique prompt name used as a lookup key.</param>
/// <param name="Description">Human-readable description of what the prompt does.</param>
/// <param name="Arguments">Structured argument descriptors accepted by the prompt template.</param>
public sealed record McpPrompt(
    string Name,
    string Description,
    IReadOnlyList<McpPromptArgument> Arguments);
