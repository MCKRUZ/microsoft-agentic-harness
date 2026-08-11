using Domain.AI.Governance;

namespace Application.AI.Common.Interfaces.Governance;

/// <summary>
/// Scans the aggregated MCP tool surface — every tool from every server, together — for threats that
/// only exist when tools are compared against each other or against their own past: tool name
/// collision, cross-server shadowing, and definition drift (rug pull). Complements
/// <see cref="IMcpSecurityScanner"/>, which inspects one tool definition in isolation and cannot see
/// any of these.
/// </summary>
public interface IMcpToolSurfaceScanner
{
    /// <summary>
    /// Scans the given tool surface and returns every structural finding. A definition-drift check is
    /// a side effect of the call: the tools passed in become the new baseline for the next call,
    /// which is why this must be called with the complete, final tool surface exactly once per build
    /// rather than speculatively.
    /// </summary>
    /// <param name="tools">Every tool on the surface, attributed to the server that advertised it.</param>
    IReadOnlyList<McpSurfaceFinding> ScanSurface(IReadOnlyList<McpSurfaceTool> tools);
}
