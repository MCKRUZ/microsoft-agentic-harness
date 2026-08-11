using Domain.Common.Config.AI;

namespace Domain.AI.Governance;

/// <summary>
/// A finding produced by comparing tools <em>against each other</em> or against a tool's own past —
/// tool name collision, cross-server shadowing, and definition drift (rug pull). Distinct from
/// <see cref="McpToolThreat"/>, which is produced by inspecting one tool definition in isolation.
/// </summary>
/// <param name="ThreatType">
/// <see cref="McpThreatType.ToolNameCollision"/>, <see cref="McpThreatType.ToolShadowing"/>, or
/// <see cref="McpThreatType.RugPull"/>.
/// </param>
/// <param name="Severity">The threat's severity.</param>
/// <param name="Description">Human-readable finding text, naming which surface changed for a drift
/// finding. Never includes attacker-supplied text — the same rule the per-tool scanner follows.
/// </param>
/// <param name="Confidence">Confidence score in [0, 1].</param>
/// <param name="InvolvedTools">
/// The tool(s) the finding is about — two entries for a collision or a shadowing reference, one for
/// drift.
/// </param>
public sealed record McpSurfaceFinding(
    McpThreatType ThreatType,
    ThreatLevel Severity,
    string Description,
    double Confidence,
    IReadOnlyList<McpSurfaceToolReference> InvolvedTools);

/// <summary>Identifies one tool involved in an <see cref="McpSurfaceFinding"/>.</summary>
/// <param name="ServerName">The server that advertised the tool, or <see langword="null"/> for a
/// first-party tool.</param>
/// <param name="ToolName">The tool's name.</param>
public sealed record McpSurfaceToolReference(string? ServerName, string ToolName);
