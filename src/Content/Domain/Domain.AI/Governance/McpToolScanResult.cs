namespace Domain.AI.Governance;

/// <summary>
/// The outcome of a security scan on an MCP tool definition.
/// Immutable value object returned by <c>IMcpSecurityScanner</c>.
/// </summary>
public sealed record McpToolScanResult(
    string ToolName,
    bool IsSafe,
    IReadOnlyList<McpToolThreat> Threats)
{
    /// <summary>Creates a safe (no threats) result.</summary>
    public static McpToolScanResult Safe(string toolName) =>
        new(toolName, true, []);
}

/// <summary>
/// A single threat finding from an MCP tool security scan.
/// </summary>
public sealed record McpToolThreat(
    McpThreatType ThreatType,
    ThreatLevel Severity,
    string Description,
    double Confidence);
