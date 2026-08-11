namespace Domain.AI.Governance;

/// <summary>
/// One tool definition as it appears on the aggregated MCP tool surface, carrying the server
/// attribution that <see cref="McpToolScanResult"/> does not — cross-server checks (name collision,
/// shadowing, definition drift) cannot be expressed without knowing which server advertised which
/// tool.
/// </summary>
/// <param name="ServerName">
/// The MCP server that advertised this tool, or <see langword="null"/> for a first-party tool
/// resolved from keyed DI rather than discovered from an external server.
/// </param>
/// <param name="ToolName">The tool's name, as advertised.</param>
/// <param name="Description">The tool's description text.</param>
/// <param name="Schema">Optional JSON schema string for the tool's parameters.</param>
public sealed record McpSurfaceTool(string? ServerName, string ToolName, string Description, string? Schema);
