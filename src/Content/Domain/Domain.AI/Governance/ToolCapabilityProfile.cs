using Domain.Common.Config.AI.Governance;

namespace Domain.AI.Governance;

/// <summary>
/// Where a tool's capability declaration came from, in the precedence order it is resolved.
/// </summary>
/// <remarks>
/// Kept separate from the capability bits themselves — the same reason <see cref="ToolBehaviorSource"/>
/// is separate from <see cref="ToolBehavior.ReadOnly"/> — because a finding must be able to say
/// <em>how sure</em> the classification is ("this tool declared itself" vs. "this tool's name matched a
/// keyword"), and because <see cref="Unclassified"/> is itself a reportable fact: it is the metric that
/// shows how much of the tool estate this check cannot see at all.
/// </remarks>
public enum ToolCapabilityOrigin
{
    /// <summary>
    /// Nothing classified the tool. Resolves to <see cref="ToolCompositionCapability.None"/> — a deliberate
    /// fail-open, not a fail-closed, unlike almost everything else this harness classifies as unknown.
    /// See <c>ToolCapabilityResolver</c>'s remarks for why: a fail-closed "unknown means both a source
    /// and a sink" would flag every agent holding two or more unclassified tools, which is exactly the
    /// "universal taint destroys signal" failure this feature exists to avoid.
    /// </summary>
    Unclassified = 0,

    /// <summary>The tool is registered in this process and declared its capabilities directly via
    /// <c>ITool.Capabilities</c>.</summary>
    FirstParty = 1,

    /// <summary>
    /// Inferred from the tool's MCP behaviour annotation (<c>openWorldHint: true</c> contributes
    /// <see cref="ToolCompositionCapability.IngestsUntrustedInput"/>). Believed from any source, unlike a
    /// loosening <c>readOnlyHint</c> claim — an open-world claim only ever adds friction to this
    /// check, so a hostile server gains nothing by asserting it.
    /// </summary>
    McpAnnotation = 2,

    /// <summary>
    /// Matched by the narrow, built-in keyword vocabulary against the tool's published name. The
    /// weakest signal, and the one most likely to be wrong in either direction — see
    /// <c>ToolCapabilityKeywordRules</c>.
    /// </summary>
    KeywordHeuristic = 3,

    /// <summary>
    /// Set explicitly by the operator, per tool or per server, in configuration. Authoritative — an
    /// operator override always wins over every other source. See <c>ToolCompositionGatingConfig</c>.
    /// </summary>
    OperatorOverride = 4,
}

/// <summary>
/// What a tool has been classified as capable of, and where that classification came from.
/// </summary>
/// <param name="ToolName">The tool's published name, as it appears in the assembled tool set.</param>
/// <param name="Capabilities">The resolved capability flags. <see cref="ToolCompositionCapability.None"/> when
/// unclassified.</param>
/// <param name="Origin">Which source produced this classification.</param>
/// <param name="ServerName">The MCP server that advertised this tool, or <see langword="null"/> for a
/// first-party tool. Carried for the same reason <see cref="ToolBehavior.ServerName"/> is: a per-server
/// operator override needs to know which server a tool actually came from.</param>
public sealed record ToolCapabilityProfile(
    string ToolName,
    ToolCompositionCapability Capabilities,
    ToolCapabilityOrigin Origin,
    string? ServerName = null)
{
    /// <summary>The profile of a tool nothing could classify: no capability bits, origin
    /// <see cref="ToolCapabilityOrigin.Unclassified"/>.</summary>
    public static ToolCapabilityProfile Unclassified(string toolName) =>
        new(toolName, ToolCompositionCapability.None, ToolCapabilityOrigin.Unclassified);
}
