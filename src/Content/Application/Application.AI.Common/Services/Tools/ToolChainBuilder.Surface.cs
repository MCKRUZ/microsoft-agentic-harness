using System.Diagnostics;
using Application.AI.Common.Helpers;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Governance;
using Domain.AI.Telemetry.Conventions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Tools;

/// <summary>
/// MCP tool-surface security policy: decides which tools in a merged, multi-skill surface survive —
/// first-party precedence, then cross-server collision/shadowing/drift findings — and turns each
/// finding into a withhold decision.
/// </summary>
public partial class ToolChainBuilder
{
    /// <summary>
    /// Resolves and deduplicates the merged tool set, replacing a blind first-wins dedup with an
    /// attributed one. Without this, two MCP servers advertising a tool under the same name resolve
    /// purely on skill iteration order — whichever happened to be gathered first silently wins and the
    /// collision is never recorded anywhere.
    /// </summary>
    /// <remarks>
    /// Two policies apply, and they are deliberately different. A first-party tool colliding with an
    /// MCP-advertised tool of the same name is not a security question — the first-party tool always
    /// wins, silently, because withholding it would let a hostile server disable one of the harness's
    /// own tools just by claiming its name. Two <em>different</em> MCP servers colliding with each
    /// other is the case surface scanning exists for: neither can be vouched for over the other, so
    /// (per the scanner's policy) both are withheld. First-party precedence is decided purely by each
    /// <see cref="ProvisionedTool"/>'s own provenance tag, never by comparing tool content — an
    /// attacker who copies a first-party tool's description verbatim onto a same-named MCP tool cannot
    /// make the two indistinguishable, because origin was recorded at resolution time, not
    /// reconstructed from what the tools say about themselves.
    /// </remarks>
    private (List<AITool> Tools, HashSet<string> McpAttributedNames) ResolveSurvivingTools(List<ProvisionedTool> allProvisioned)
    {
        var firstPartyNames = CollectFirstPartyNames(allProvisioned);

        // One canonical (server, name)-deduplicated view of every MCP candidate, computed once and
        // used for BOTH the scan input and the final publish selection below — so the instance the
        // surface scanner evaluated is guaranteed to be the instance that reaches the model. Two
        // independent first-occurrence picks (one for scanning, one for publishing) could otherwise
        // disagree if the same server was contacted twice within one build and returned a changed
        // definition in between the two calls.
        var mcpCandidates = DeduplicateMcpCandidates(allProvisioned, firstPartyNames);

        var survivingNames = new HashSet<string>(firstPartyNames, StringComparer.OrdinalIgnoreCase);

        if (_surfaceScanner is null || _aiConfig?.CurrentValue.Governance.EnableMcpSecurity != true)
            foreach (var candidate in mcpCandidates)
                survivingNames.Add(candidate.Tool.Name);
        else
            AddScannedMcpNames(mcpCandidates, survivingNames);

        return ProjectSurvivors(allProvisioned, mcpCandidates, survivingNames, firstPartyNames);
    }

    private static HashSet<string> CollectFirstPartyNames(List<ProvisionedTool> allProvisioned)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in allProvisioned)
            if (p.McpServerName is null)
                names.Add(p.Tool.Name);

        return names;
    }

    /// <summary>
    /// The canonical MCP-sourced candidate set: one entry per (server, name) pair, excluding any name a
    /// first-party tool already claims (that case is resolved by provenance alone — it never reaches the
    /// scanner or the published surface as an MCP entry).
    /// </summary>
    /// <remarks>
    /// Grouped by (server, name) together — NOT by name alone, and NOT by a concatenated string key.
    /// Grouping by name alone would collapse two genuinely different servers' same-named tools down to a
    /// single candidate before the surface scanner ever saw more than one of them, silently discarding
    /// the exact collision this scan exists to catch. A concatenated string key has its own version of
    /// the same bug: server "trusted" + tool "reader" and server "trustedread" + tool "er" would hash
    /// identically. A tuple key compares both components independently, so no such collision is
    /// possible. Grouping still only removes true duplicates: the same server's tool recorded twice
    /// because two different resolution paths reached it.
    /// </remarks>
    private static List<ProvisionedTool> DeduplicateMcpCandidates(List<ProvisionedTool> allProvisioned, HashSet<string> firstPartyNames)
        => allProvisioned
            .Where(p => p.McpServerName is not null && !firstPartyNames.Contains(p.Tool.Name))
            .GroupBy(p => (Server: p.McpServerName!.ToUpperInvariant(), Name: p.Tool.Name.ToUpperInvariant()))
            .Select(g => g.First())
            .ToList();

    /// <summary>
    /// Runs the surface scanner over the canonical MCP candidate set and admits whatever the withhold
    /// policy leaves standing.
    /// </summary>
    private void AddScannedMcpNames(List<ProvisionedTool> mcpCandidates, HashSet<string> survivingNames)
    {
        var surface = mcpCandidates
            .Select(p => new McpSurfaceTool(p.McpServerName, p.Tool.Name, p.Tool.Description, AIToolSchemaText.Extract(p.Tool)))
            .ToList();

        var findings = _surfaceScanner!.ScanSurface(surface);
        var withheldNames = ApplySurfaceFindings(findings);

        // Only a rug-pull finding that was actually withheld must block its baseline from advancing —
        // that is what makes StrictDriftMode's withhold durable instead of self-clearing on the very
        // next scan. Everything else (unchanged, first-seen, a drift finding that was flagged and
        // continued, or a tool withheld for a collision/shadowing reason unrelated to its own
        // definition) commits normally.
        var withheldDriftTools = findings
            .Where(f => f.ThreatType == McpThreatType.RugPull && withheldNames.Contains(f.InvolvedTools[0].ToolName))
            .Select(f => f.InvolvedTools[0])
            .ToHashSet();

        _surfaceScanner.CommitDefinitionPins(surface, withheldDriftTools);

        foreach (var candidate in mcpCandidates)
            if (!withheldNames.Contains(candidate.Tool.Name))
                survivingNames.Add(candidate.Tool.Name);
    }

    /// <summary>
    /// Projects the final tool list: first-party entries straight from the raw surface (a name must
    /// have survived; at most one instance is expected per name), then MCP entries drawn from the same
    /// deduplicated <paramref name="mcpCandidates"/> the scanner evaluated — never re-derived from the
    /// raw, un-deduplicated surface a second time, so the published instance can never diverge from the
    /// one that was actually scanned. Also returns which surviving names are MCP-attributed, decided in
    /// the same pass rather than re-derived by the caller.
    /// </summary>
    private static (List<AITool> Tools, HashSet<string> McpAttributedNames) ProjectSurvivors(
        List<ProvisionedTool> allProvisioned,
        List<ProvisionedTool> mcpCandidates,
        HashSet<string> survivingNames,
        HashSet<string> firstPartyNames)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<AITool>();

        foreach (var p in allProvisioned)
        {
            if (p.McpServerName is not null)
                continue;
            if (!survivingNames.Contains(p.Tool.Name))
                continue;
            if (seen.Add(p.Tool.Name))
                result.Add(p.Tool);
        }

        var mcpAttributedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in mcpCandidates)
        {
            // A name a first-party tool also claims never survives here — DeduplicateMcpCandidates
            // already excluded it from mcpCandidates, so this loop only ever sees names with no
            // first-party claim, and the first-party-wins policy above is already fully applied.
            if (!survivingNames.Contains(candidate.Tool.Name))
                continue;
            if (seen.Add(candidate.Tool.Name))
            {
                result.Add(candidate.Tool);
                mcpAttributedNames.Add(candidate.Tool.Name);
            }
        }

        return (result, mcpAttributedNames);
    }

    /// <summary>
    /// Applies withhold policy per finding type and returns the tool names to exclude from the final
    /// surface. Collision is a hard rule — always withheld, never threshold-gated, because "which one
    /// is legitimate" cannot be answered from the definitions alone. Shadowing and drift go through
    /// the same severity/threshold mechanism the per-tool scanner already uses, so operators tune one
    /// knob for both. Drift additionally respects <c>GovernanceConfig.McpToolSurfaceScanning.StrictDriftMode</c>:
    /// off by default (flag-and-continue — a legitimate upstream update must not break a running host),
    /// on to withhold a drifted definition until it is re-approved.
    /// </summary>
    private HashSet<string> ApplySurfaceFindings(IReadOnlyList<McpSurfaceFinding> findings)
    {
        var withheld = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var threshold = _aiConfig!.CurrentValue.Governance.McpToolBlockThreshold;
        var strictDrift = _aiConfig.CurrentValue.Governance.McpToolSurfaceScanning.StrictDriftMode;

        foreach (var finding in findings)
        {
            switch (finding.ThreatType)
            {
                case McpThreatType.ToolNameCollision:
                    foreach (var tool in finding.InvolvedTools)
                        withheld.Add(tool.ToolName);
                    RecordSurfaceFinding(GovernanceMetrics.McpToolCollisions, finding);
                    _logger.LogWarning("MCP tool surface: {Finding}", finding.Description);
                    break;

                case McpThreatType.ToolShadowing:
                    RecordSurfaceFinding(GovernanceMetrics.McpToolShadowing, finding);
                    LogAndMaybeWithhold(finding, finding.Severity >= threshold, withheld);
                    break;

                case McpThreatType.RugPull:
                    RecordSurfaceFinding(GovernanceMetrics.McpToolDrift, finding);
                    LogAndMaybeWithhold(finding, strictDrift && finding.Severity >= threshold, withheld);
                    break;
            }
        }

        return withheld;
    }

    private static void RecordSurfaceFinding(System.Diagnostics.Metrics.Counter<long> counter, McpSurfaceFinding finding)
        => counter.Add(1, new TagList { { GovernanceConventions.McpThreatSeverityTag, finding.Severity.ToString() } });

    private void LogAndMaybeWithhold(McpSurfaceFinding finding, bool withhold, HashSet<string> withheld)
    {
        if (withhold)
        {
            withheld.Add(finding.InvolvedTools[0].ToolName);
            _logger.LogWarning("MCP tool surface: {Finding}", finding.Description);
        }
        else
        {
            _logger.LogInformation("MCP tool surface: {Finding}", finding.Description);
        }
    }
}
