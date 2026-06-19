using Domain.AI.Changes;

namespace Application.AI.Common.Interfaces.Tools;

/// <summary>
/// The risk profile of a tool: its blast radius and whether it is read-only. Together these
/// are the inputs the graded-autonomy evaluator needs to decide whether a tool call may
/// auto-approve under the active tier.
/// </summary>
/// <param name="Radius">The tool's declared <see cref="ITool.RiskTier"/> blast radius.</param>
/// <param name="IsReadOnly">The tool's <see cref="ITool.IsReadOnly"/> flag — the inverse of
/// "is a state change" for the evaluator's state-changer safety rule.</param>
public readonly record struct ToolRiskProfile(BlastRadius Radius, bool IsReadOnly)
{
    /// <summary>
    /// The fail-safe profile for a tool that cannot be classified (e.g. an external MCP tool
    /// with no local registration): <see cref="BlastRadius.Medium"/> and treated as a state
    /// change (not read-only). Neither loosens governance.
    /// </summary>
    public static ToolRiskProfile Default => new(BlastRadius.Medium, IsReadOnly: false);
}

/// <summary>
/// Resolves a tool's <see cref="ToolRiskProfile"/> from its name. Lets governance behaviors
/// reason about a tool's blast radius without depending on how tools are registered or
/// resolved.
/// </summary>
/// <remarks>
/// Implementations resolve the registered <see cref="ITool"/> for the name and read its
/// <see cref="ITool.RiskTier"/> / <see cref="ITool.IsReadOnly"/>. Unknown names (external
/// MCP tools, typos) return <see cref="ToolRiskProfile.Default"/> — fail-safe, never
/// fail-open.
/// </remarks>
public interface IToolRiskClassifier
{
    /// <summary>
    /// Classifies the named tool. Returns <see cref="ToolRiskProfile.Default"/> when the
    /// name does not resolve to a registered tool.
    /// </summary>
    /// <param name="toolName">The tool name / keyed-DI registration key.</param>
    /// <returns>The tool's risk profile, or the fail-safe default.</returns>
    ToolRiskProfile Classify(string toolName);
}
