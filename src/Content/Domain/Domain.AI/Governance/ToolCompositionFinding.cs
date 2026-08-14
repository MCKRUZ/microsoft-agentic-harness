using Domain.Common.Config.AI.Governance;

namespace Domain.AI.Governance;

/// <summary>
/// One co-resident source/sink pairing found in an agent's assembled tool set: a tool carrying a
/// source capability alongside a different tool carrying a sink capability.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is a fact, not a verdict — deliberately, and it is the whole reason the build-time
/// analysis is safe.</strong> It carries no <c>Posture</c>. Which posture applies to
/// (<see cref="SourceCapability"/>, <see cref="SinkCapability"/>) is looked up fresh, every time it
/// matters, against the live <c>ToolCompositionGatingConfig</c> — once by
/// <c>ToolCompositionReporter</c> at build time (to decide what is worth reporting) and independently
/// again by <c>ToolInvocationGovernor.RequiresApprovalForToolComposition</c> at call time (to decide
/// whether to gate). Both read <c>ToolCompositionPostureResolver.Resolve</c>, the one place that
/// mapping is decided, so an operator's config change takes effect for an already-built agent the next
/// time its sink tool is called — without a rebuild — for exactly the same reason #324's behaviour
/// posture does: the STRUCTURAL fact (what a tool declares, or here, which tools are co-resident) is
/// fixed once discovered; the POLICY response to that fact is read live.
/// </para>
/// <para>
/// The corollary limit, worth stating plainly: a config change that alters WHICH tools count as a
/// source or a sink (a new keyword rule, a new <c>ToolCapabilities</c>/<c>ServerCapabilities</c>
/// override) changes the fact itself, not just the policy response to it, and does need an agent
/// rebuild to take effect — the same limit #324's tool-behaviour registry already accepts for a
/// tool's declared annotations.
/// </para>
/// </remarks>
/// <param name="SourceTool">The tool that can bring untrusted or sensitive content into the agent's
/// context.</param>
/// <param name="SourceCapability">Which source bit triggered this finding. Exactly one bit, even when
/// <paramref name="SourceTool"/>'s full profile carries more than one — a finding names one bit-pair so
/// an operator overriding one pairing's posture does not silently affect another.</param>
/// <param name="SinkTool">The tool that can act on that content in a way that costs something.
/// Always a different tool from <paramref name="SourceTool"/> — see <c>ToolCompositionAnalyzer</c>'s
/// remarks on why self-pairs (one tool that is both) are excluded from this check.</param>
/// <param name="SinkCapability">Which sink bit triggered this finding. Exactly one bit, for the same
/// reason as <paramref name="SourceCapability"/>.</param>
/// <param name="SourceOrigin">Where <paramref name="SourceTool"/>'s classification came from.</param>
/// <param name="SinkOrigin">Where <paramref name="SinkTool"/>'s classification came from.</param>
public sealed record ToolCompositionFinding(
    string SourceTool,
    ToolCompositionCapability SourceCapability,
    string SinkTool,
    ToolCompositionCapability SinkCapability,
    ToolCapabilityOrigin SourceOrigin,
    ToolCapabilityOrigin SinkOrigin)
{
    /// <summary>
    /// A stable, human-readable description of the finding: both tools and the capability pairing that
    /// implicated them. This is the string an approver reads, the string that goes into the audit log,
    /// and the acceptance-criteria requirement that a finding "name both tools and the path".
    /// </summary>
    public string Path => $"{SourceTool} ({SourceCapability}) → {SinkTool} ({SinkCapability})";
}

/// <summary>
/// The immutable result of analyzing one agent's assembled tool set for composition risk, carried from
/// build time — where the tool set is known — to call time, where the posture is enforced live. See
/// <c>ToolChainBuilder</c>'s stamping of this onto <c>GovernedAIFunction</c> and
/// <c>ToolInvocationGovernor.RequiresApprovalForToolComposition</c>'s consumption of it.
/// </summary>
/// <param name="Findings">
/// Every co-residency fact for the tool set this taint was computed over, <strong>regardless of
/// current posture</strong> — see <see cref="ToolCompositionFinding"/>'s remarks for why filtering by
/// posture here would silently break a live config change. A sink tool's <c>GovernedAIFunction</c>
/// wrapper carries only the findings that name it as the sink — see
/// <c>ToolChainBuilder.ApplyCompositionTaint</c> — so the governor never has to search the whole list
/// per call.
/// </param>
public sealed record ToolCompositionTaint(IReadOnlyList<ToolCompositionFinding> Findings)
{
    /// <summary>No findings implicate this tool. The common case — every non-sink tool, and every sink
    /// tool with no co-resident source anywhere in the same tool set.</summary>
    public static ToolCompositionTaint None { get; } = new([]);
}
