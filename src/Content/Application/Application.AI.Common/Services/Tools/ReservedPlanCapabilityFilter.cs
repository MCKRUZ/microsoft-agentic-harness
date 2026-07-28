using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Planner;
using Domain.AI.Telemetry.Conventions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Tools;

/// <summary>
/// Drops tools whose names collide with a reserved <see cref="PlanCapabilities"/> name before they reach
/// an agent's callable surface, reporting each drop to the log and the governance-violation counter.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is shared rather than inlined.</strong> Plan capabilities are authorized by name out
/// of the same flat, case-insensitively matched string space that <c>CapabilityEnvelope.AllowedTools</c>
/// draws tool names from, so a tool published as <c>rag_retrieval</c> would be handed to the model by any
/// envelope granting retrieval — and an envelope granting that tool would grant plan inference. Closing
/// that requires the check to run on <em>every</em> channel onto the callable surface, not just one.
/// <see cref="ToolChainBuilder"/> is the main channel, but the framework also merges
/// <c>AIContext.Tools</c> contributed by <c>AIContextProvider</c>s (progressive skill disclosure, and any
/// provider a consumer adds), which never pass through the builder. Both call here so the two cannot
/// drift apart as the harness is extended.
/// </para>
/// <para>
/// <strong>Drop, never throw.</strong> The names this guards against arrive at runtime from third-party
/// sources — MCP servers and plugin manifests — that a boot-time DI scan cannot see. A third party
/// editing its tool list must not be able to take down every agent turn in the host, so a collision
/// degrades to one loud <c>Error</c> log plus a governance-violation counter and the run continues
/// without that tool. <c>ReservedPlanCapabilityGuard</c> stays the louder, fail-fast check for the
/// first-party keyed registrations the host controls.
/// </para>
/// </remarks>
public static class ReservedPlanCapabilityFilter
{
    /// <summary>
    /// Telemetry policy tag identifying a drop made by this filter, so a governance dashboard can
    /// separate this fail-open closure from ordinary policy violations.
    /// </summary>
    public const string PolicyName = "reserved_plan_capability";

    /// <summary>
    /// Returns the tools that may be published to the model: every tool in <paramref name="tools"/>
    /// except those whose name matches a reserved plan-capability name. Each exclusion is logged and
    /// counted.
    /// </summary>
    /// <param name="tools">The candidate tools for one callable surface.</param>
    /// <param name="source">
    /// Human-readable description of where the tools were resolved from, recorded on the drop log so the
    /// source that needs re-keying is identifiable.
    /// </param>
    /// <param name="logger">Logger that receives the collision report.</param>
    /// <returns>The permitted tools, in their original order.</returns>
    public static List<AITool> Exclude(IEnumerable<AITool> tools, string source, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(logger);

        var permitted = new List<AITool>();
        foreach (var tool in tools)
        {
            if (PlanCapabilities.IsReserved(tool.Name))
                ReportCollision(tool.Name, source, logger);
            else
                permitted.Add(tool);
        }

        return permitted;
    }

    /// <summary>
    /// Records a runtime-sourced tool that was dropped for colliding with a reserved plan-capability
    /// name — loudly, because it means a source is publishing a name the plan engine owns and that
    /// source needs re-keying.
    /// </summary>
    private static void ReportCollision(string toolName, string source, ILogger logger)
    {
        var reserved = PlanCapabilities.ReservedNames
            .First(name => string.Equals(name, toolName, StringComparison.OrdinalIgnoreCase));

        logger.LogError(
            "Reserved plan-capability collision: tool '{ToolName}' from {ToolSource} matches reserved " +
            "plan capability '{ReservedName}' and was excluded from the callable tool chain. Plan " +
            "capabilities are authorized out of the same CapabilityEnvelope.AllowedTools string space as " +
            "tool names, so granting that capability would otherwise also grant this tool. Re-key the tool " +
            "at its source.",
            toolName, source, reserved);

        // Tagged with the normalised reserved name rather than the verbatim tool name: a case variant is
        // attacker-influenced text and would put unbounded cardinality on the metric.
        GovernanceMetrics.Violations.Add(1,
            new KeyValuePair<string, object?>(GovernanceConventions.PolicyName, PolicyName),
            new KeyValuePair<string, object?>(GovernanceConventions.ToolName, reserved));
    }
}
