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

    /// <summary>Records the current definition hash pair as the new baseline for the tool.</summary>
    /// <param name="serverName">The server that advertised the tool, or <see langword="null"/> for a
    /// first-party tool.</param>
    /// <param name="toolName">The tool's name.</param>
    /// <param name="pin">The definition hash pair to record.</param>
    void Set(string? serverName, string toolName, McpToolDefinitionPin pin);

    /// <summary>
    /// Atomically records <paramref name="pin"/> as the new baseline and returns whatever pin was
    /// previously recorded, or <see langword="null"/> for a tool seen for the first time.
    /// </summary>
    /// <remarks>
    /// The drift check must read the prior baseline and write the new one as a single unit: two
    /// concurrent scans of the same tool (the store is a process-lifetime singleton and multiple agent
    /// turns can build tool sets at once) calling <see cref="TryGet"/> then <see cref="Set"/>
    /// separately could both read the same stale baseline before either write lands, each
    /// independently — and redundantly — deciding the definition drifted for what is really one
    /// transition. This method closes that race by making the read-then-write indivisible.
    /// </remarks>
    /// <param name="serverName">The server that advertised the tool, or <see langword="null"/> for a
    /// first-party tool.</param>
    /// <param name="toolName">The tool's name.</param>
    /// <param name="pin">The definition hash pair to record as the new baseline.</param>
    McpToolDefinitionPin? GetAndSet(string? serverName, string toolName, McpToolDefinitionPin pin);
}
