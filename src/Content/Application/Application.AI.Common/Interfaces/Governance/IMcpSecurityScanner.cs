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

    /// <summary>
    /// Scans a block of foreign-authored content that isn't shaped like an MCP tool definition — a
    /// plugin skill's name/description/instructions, or an agent manifest's — using the same
    /// injection rules as <see cref="ScanTool"/>, plus rules scoped to instruction content
    /// specifically (a directive to fetch-and-run a remote payload, or to encode and transmit data).
    /// </summary>
    /// <param name="sourceName">
    /// Identifies what was scanned for logging and findings — a skill id or agent id.
    /// </param>
    /// <param name="content">The text to scan.</param>
    /// <param name="includeLengthSensitiveRules">
    /// Whether to run rules whose false-positive profile depends on document length — today, just the
    /// long-base64-run rule. Pass <see langword="true"/> for short fields (name, description), the
    /// same shape as a tool description. Pass <see langword="false"/> for long-form content (a
    /// manifest's instructions body, which routinely contains legitimate 40+ character tokens — a
    /// hash, a UUID, an embedded credential placeholder — that would otherwise false-positive).
    /// </param>
    /// <returns>Scan result with any detected threats.</returns>
    McpToolScanResult ScanContent(string sourceName, string content, bool includeLengthSensitiveRules);
}
