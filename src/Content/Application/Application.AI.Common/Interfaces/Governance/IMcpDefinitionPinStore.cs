using Domain.AI.Governance;

namespace Application.AI.Common.Interfaces.Governance;

/// <summary>
/// Persists the last-seen definition hash for each MCP tool, keyed by server and tool name, so a
/// rug-pull check can tell a definition that changed from one seen for the first time.
/// </summary>
public interface IMcpDefinitionPinStore
{
    /// <summary>
    /// Returns the previously-recorded pin for the given tool, or <see langword="null"/> if this is
    /// the first time the store has seen it — which must never be treated as a drift finding.
    /// </summary>
    /// <param name="serverName">The server that advertised the tool, or <see langword="null"/> for a
    /// first-party tool.</param>
    /// <param name="toolName">The tool's name.</param>
    McpToolDefinitionPin? TryGet(string? serverName, string toolName);

    /// <summary>
    /// Records the current definition hash pair as the new baseline for the tool. The caller decides
    /// when this may run — a rug-pulled tool whose finding was withheld must not be committed, or the
    /// withhold would only last until the next scan (see <see cref="IMcpToolSurfaceScanner.CommitDefinitionPins"/>).
    /// </summary>
    /// <param name="serverName">The server that advertised the tool, or <see langword="null"/> for a
    /// first-party tool.</param>
    /// <param name="toolName">The tool's name.</param>
    /// <param name="pin">The definition hash pair to record.</param>
    void Set(string? serverName, string toolName, McpToolDefinitionPin pin);
}
