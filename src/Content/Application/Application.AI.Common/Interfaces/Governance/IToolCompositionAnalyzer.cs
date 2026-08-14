using Domain.AI.Governance;
using Domain.Common.Config.AI.Governance;
using Microsoft.Extensions.AI;

namespace Application.AI.Common.Interfaces.Governance;

/// <summary>
/// Analyzes an agent's assembled tool set for a dangerous <em>combination</em> — an untrusted-input or
/// credential-reading tool co-resident with a tool that writes files, executes code, or sends data
/// outbound. Every individual tool-governance control (allow/deny lists, per-agent authorization,
/// behaviour gating) asks whether a single tool may run; this asks what the whole set can do together,
/// which is the shape an indirect-prompt-injection exfiltration primitive actually takes.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Runs at build time, over the whole assembled set, not per call.</strong> A single tool
/// carries no information about whether a dangerous pairing exists — only the set does. Called from
/// <c>ToolChainBuilder</c>'s three whole-agent-set exits, never from a per-tool or per-skill resolution
/// step, which would only ever see a fragment of the eventual set and could neither confirm nor rule
/// out a cross-skill pairing.
/// </para>
/// <para>
/// <strong>The control case is true by construction.</strong> An agent holding only a source-capable
/// tool, or only a sink-capable tool, produces an empty opposite-side set, so the cross product that
/// produces findings is empty — there is no code path by which a one-sided tool set can produce a
/// finding. This is the mandated control in the issue's acceptance criteria, and it is a property of
/// the algorithm's shape rather than a filter applied after the fact.
/// </para>
/// </remarks>
public interface IToolCompositionAnalyzer
{
    /// <summary>
    /// Analyzes <paramref name="tools"/> for every co-resident source/sink pairing — a structural fact
    /// about the set, computed independently of the currently-configured posture. See
    /// <see cref="ToolCompositionAssessment.Findings"/>'s remarks for why filtering by posture happens
    /// later, live, rather than here.
    /// </summary>
    /// <param name="tools">The agent's fully assembled tool set.</param>
    /// <returns>Every co-residency fact, and the names of tools nothing could classify.</returns>
    ToolCompositionAssessment Analyze(IReadOnlyList<AITool> tools);
}

/// <summary>
/// The result of analyzing one agent's tool set for composition risk.
/// </summary>
/// <param name="Findings">
/// Every co-resident source/sink pairing found, <strong>regardless of current posture</strong> — a
/// pairing under <see cref="CompositionPosture.Allow"/> is included here just like any other. Posture
/// filtering happens live, separately, at the two places that actually need a verdict:
/// <c>ToolCompositionReporter</c> (what to report at build time) and
/// <c>ToolInvocationGovernor.RequiresApprovalForToolComposition</c> (whether to gate at call time). See
/// <c>ToolCompositionFinding</c>'s remarks for why baking the posture in here would silently break a
/// live config change on an already-built agent.
/// Capped at 50 with <see cref="Truncated"/> set when the true count exceeds the cap — a set with
/// enough distinct source and sink tools to exceed it is already a configuration worth a human's
/// attention regardless of the exact count.
/// </param>
/// <param name="UnclassifiedTools">
/// Tools nothing could classify — the blind spot this check's deliberate fail-open produces. Reported
/// rather than silently absorbed, so an operator can see how much of the tool set this analysis
/// actually covers.
/// </param>
/// <param name="Truncated">Whether <see cref="Findings"/> was capped before every real finding was emitted.</param>
public sealed record ToolCompositionAssessment(
    IReadOnlyList<ToolCompositionFinding> Findings,
    IReadOnlyList<string> UnclassifiedTools,
    bool Truncated = false)
{
    /// <summary>No findings, no unclassified tools — the assessment of an empty or fully-benign set.</summary>
    public static ToolCompositionAssessment Empty { get; } = new([], []);
}
