using Domain.AI.Governance;

namespace Application.AI.Common.Interfaces.Governance;

/// <summary>
/// Scans MCP tool definitions for security threats including tool poisoning,
/// typosquatting, hidden instructions, and description injection.
/// </summary>
public interface IMcpSecurityScanner
{
    /// <summary>
    /// Scans a single MCP tool definition for security threats.
    /// </summary>
    /// <param name="toolName">The tool name to scan.</param>
    /// <param name="toolDescription">The tool's description text.</param>
    /// <param name="toolSchema">Optional JSON schema string for the tool's parameters.</param>
    /// <returns>Scan result with any detected threats.</returns>
    McpToolScanResult ScanTool(string toolName, string toolDescription, string? toolSchema = null);

    /// <summary>
    /// Scans multiple MCP tool definitions in batch.
    /// </summary>
    IReadOnlyList<McpToolScanResult> ScanTools(IEnumerable<(string Name, string Description, string? Schema)> tools);
}
