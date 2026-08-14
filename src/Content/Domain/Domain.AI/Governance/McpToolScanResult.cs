using System.Linq;
using Domain.Common.Config.AI;

namespace Domain.AI.Governance;

/// <summary>
/// The outcome of a security scan on an MCP tool definition, or on any other text screened by the
/// same scanner (a skill or agent manifest's name, description, or instructions). Immutable value
/// object returned by <c>IMcpSecurityScanner</c>.
/// </summary>
/// <remarks>
/// <c>ToolName</c> names whatever was scanned — an MCP tool, but also a skill id or agent id when a
/// manifest is what triggered the scan. Kept as-is rather than renamed to a more generic term: this
/// type is shared across <c>ScanningMcpToolProvider</c> and <c>McpToolSurfaceScannerAdapter</c>, and
/// a rename for a caller that doesn't need one would be an unrelated ripple.
/// </remarks>
public sealed record McpToolScanResult(
    string ToolName,
    bool IsSafe,
    IReadOnlyList<McpToolThreat> Threats)
{
    /// <summary>Creates a safe (no threats) result.</summary>
    public static McpToolScanResult Safe(string toolName) =>
        new(toolName, true, []);

    /// <summary>
    /// The highest severity among <see cref="Threats"/>, or <see langword="null"/> when there are
    /// none. Null-safe by construction — a third-party <c>IMcpSecurityScanner</c> that reports
    /// <see cref="IsSafe"/> = false with an empty threat list can never make this throw.
    /// </summary>
    public ThreatLevel? HighestSeverity => Threats.Count == 0 ? null : Threats.Max(t => t.Severity);

    /// <summary>
    /// Whether <see cref="HighestSeverity"/> meets or exceeds <paramref name="threshold"/> — the
    /// shared withhold/admit decision every scanner consumer applies to a scan result. A result with
    /// no threats is never withheld, regardless of threshold.
    /// </summary>
    public bool IsWithheld(ThreatLevel threshold) => HighestSeverity is { } highest && highest >= threshold;
}

/// <summary>
/// A single threat finding from an MCP tool security scan.
/// </summary>
public sealed record McpToolThreat(
    McpThreatType ThreatType,
    ThreatLevel Severity,
    string Description,
    double Confidence);

/// <summary>
/// Formatting for <see cref="McpToolThreat"/> shared by every consumer that logs or reports a scan
/// result (<c>ScanningMcpToolProvider</c>, the skill/agent manifest parsers).
/// </summary>
public static class McpToolThreatExtensions
{
    /// <summary>
    /// Formats each threat as a stable "ThreatType/Severity" label — never <see cref="McpToolThreat.Description"/>,
    /// which can carry attacker-supplied text and must never reach a log (issue #331).
    /// </summary>
    public static IReadOnlyList<string> ToFindingLabels(this IEnumerable<McpToolThreat> threats) =>
        threats.Select(t => $"{t.ThreatType}/{t.Severity}").ToList();
}
