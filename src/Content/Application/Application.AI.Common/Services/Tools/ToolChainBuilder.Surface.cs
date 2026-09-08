using System.Collections.Concurrent;
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
    private (List<AITool> Tools, HashSet<string> McpAttributedNames) ResolveSurvivingTools(
        List<ProvisionedTool> allProvisioned, ConcurrentDictionary<AITool, byte> callOnceCandidates)
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

        return ProjectSurvivors(allProvisioned, mcpCandidates, survivingNames, firstPartyNames, callOnceCandidates);
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
        var (withheldNames, withheldDriftTools) = ApplySurfaceFindings(findings);

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
    /// <remarks>
    /// <strong>Two skills sharing a first-party tool name union both skills' egress scope (#531,
    /// #589).</strong> Each skill's tools are already wrapped as <see cref="GovernedAIFunction"/> —
    /// one instance's <c>SkillIds</c> baked in per skill — before <paramref name="allProvisioned"/>
    /// reaches this method (<c>BuildProvisionedToolsAsync</c>/<c>FinalizeChain</c> runs per skill,
    /// upstream). The first-party dedup loop below detects a second skill sharing an already-published
    /// name and re-wraps the published instance to carry the union of both skills' ids, rather than
    /// silently keeping only whichever skill enumerated first — so a call to the shared tool resolves
    /// an egress policy covering both skills' declared allowlists, regardless of which one the model
    /// is conceptually driving the turn from. The re-wrap also carries <paramref name="callOnceCandidates"/>
    /// forward onto the new instance — the same alias-preservation <see cref="WrapGoverned"/> already
    /// does — because that set is keyed by reference identity
    /// (<see cref="ReferenceEqualityComparer.Instance"/>): a re-wrap that produced a new,
    /// never-tagged instance without this would silently drop a shared tool's
    /// <c>CallOncePerConversation</c> restriction the moment two skills happened to share its name
    /// (correctness/security review finding on this PR's own first draft).
    /// </remarks>
    private static (List<AITool> Tools, HashSet<string> McpAttributedNames) ProjectSurvivors(
        List<ProvisionedTool> allProvisioned,
        List<ProvisionedTool> mcpCandidates,
        HashSet<string> survivingNames,
        HashSet<string> firstPartyNames,
        ConcurrentDictionary<AITool, byte> callOnceCandidates)
    {
        var indexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new List<AITool>();

        foreach (var p in allProvisioned)
        {
            if (p.McpServerName is not null)
                continue;
            if (!survivingNames.Contains(p.Tool.Name))
                continue;

            if (!indexByName.TryGetValue(p.Tool.Name, out var existingIndex))
            {
                indexByName[p.Tool.Name] = result.Count;
                result.Add(p.Tool);
                continue;
            }

            result[existingIndex] = UnionSkillScopeIfNeeded(result[existingIndex], p.Tool, callOnceCandidates);
        }

        var mcpAttributedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in mcpCandidates)
        {
            // A name a first-party tool also claims never survives here — DeduplicateMcpCandidates
            // already excluded it from mcpCandidates, so this loop only ever sees names with no
            // first-party claim, and the first-party-wins policy above is already fully applied.
            if (!survivingNames.Contains(candidate.Tool.Name))
                continue;
            if (indexByName.TryAdd(candidate.Tool.Name, result.Count))
            {
                result.Add(candidate.Tool);
                mcpAttributedNames.Add(candidate.Tool.Name);
            }
        }

        return (result, mcpAttributedNames);
    }

    /// <summary>
    /// When two skills share a first-party tool name, unions the second skill's <see cref="GovernedAIFunction.SkillIds"/>
    /// into the already-published instance instead of silently dropping them (#589). Re-wraps the same
    /// way <see cref="ApplyCompositionTaint"/> does — unwrap to <see cref="GovernedAIFunction.Inner"/>,
    /// rewrap with the combined scope — since this runs before that method, on tools with no
    /// composition taint yet, so there is nothing else to preserve across the rewrap.
    /// </summary>
    /// <param name="published">The instance already added to the result list for this name.</param>
    /// <param name="candidate">A later-enumerated skill's own instance of the same-named tool.</param>
    /// <param name="callOnceCandidates">
    /// The whole-agent-set call-once candidate tracker (#589 correctness/security review finding):
    /// keyed by reference identity, so a re-wrap here that produced a new, untagged instance would
    /// silently fall out of it, dropping a shared tool's <c>CallOncePerConversation</c> restriction
    /// exactly the way <see cref="WrapGoverned"/>'s own alias-carry-forward comment already warns
    /// about for its own re-wrap. If either side was tagged, the new instance is tagged too.
    /// </param>
    /// <returns>
    /// <paramref name="published"/> unchanged when either side isn't a <see cref="GovernedAIFunction"/>
    /// or the candidate's skill ids are already fully covered by the published instance; otherwise a
    /// new instance wrapping the same inner function with the union of both sides' skill ids.
    /// </returns>
    private static AITool UnionSkillScopeIfNeeded(
        AITool published, AITool candidate, ConcurrentDictionary<AITool, byte> callOnceCandidates)
    {
        // Computed once up front, but the actual TryAdd is deferred to whichever instance this
        // method actually returns (code-simplifier: tagging `published` unconditionally here, then
        // tagging `rewrapped` again below when a rewrap happens, left a stale-but-harmless entry for
        // the discarded `published` instance — RegisterSurvivingCallOnceTools only reads tags for
        // tools that make it into the final surface, so it was never a bug, just a wasted write).
        // Still evaluated before the union-needed check below, not only inside it (code-review
        // finding: a skill that names the same tool via two of its own ToolDeclarations - same skill
        // id on both, so the union branch never fires at all - could still have the DISCARDED
        // candidate be the one call-once-tagged. Since `published` is what survives to
        // RegisterSurvivingCallOnceTools either way, tag whichever instance is actually returned the
        // moment either side was a candidate, independent of whether a union rewrap also happens.
        var eitherWasCallOnceCandidate = callOnceCandidates.ContainsKey(published) || callOnceCandidates.ContainsKey(candidate);

        if (published is not GovernedAIFunction publishedGoverned || candidate is not GovernedAIFunction candidateGoverned)
        {
            if (eitherWasCallOnceCandidate)
                callOnceCandidates.TryAdd(published, 0);
            return published;
        }

        var publishedIds = publishedGoverned.SkillIds ?? [];
        var candidateIds = candidateGoverned.SkillIds ?? [];
        if (candidateIds.Count == 0 || candidateIds.All(id => publishedIds.Contains(id, StringComparer.OrdinalIgnoreCase)))
        {
            if (eitherWasCallOnceCandidate)
                callOnceCandidates.TryAdd(published, 0);
            return published;
        }

        var union = publishedIds
            .Concat(candidateIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rewrapped = new GovernedAIFunction(
            publishedGoverned.Inner, compositionTaint: null, publishedGoverned.CurrentSkillAccessor, union);

        if (eitherWasCallOnceCandidate)
            callOnceCandidates.TryAdd(rewrapped, 0);

        return rewrapped;
    }

    /// <summary>
    /// Applies withhold policy per finding type and returns the tool names to exclude from the final
    /// surface, plus — separately — exactly which tools' drift findings were withheld this build.
    /// Collision is a hard rule — always withheld, never threshold-gated, because "which one is
    /// legitimate" cannot be answered from the definitions alone. Shadowing and drift go through the
    /// same severity/threshold mechanism the per-tool scanner already uses, so operators tune one knob
    /// for both. Drift additionally respects <c>GovernanceConfig.McpToolSurfaceScanning.StrictDriftMode</c>:
    /// off by default (flag-and-continue — a legitimate upstream update must not break a running host),
    /// on to withhold a drifted definition until it is re-approved.
    /// </summary>
    /// <remarks>
    /// The second return value is deliberately its own decision, not derived from the first afterward.
    /// A tool can appear in <c>WithheldNames</c> for a reason unrelated to its own definition — e.g. a
    /// collision or shadowing finding — while its own drift finding this build was flag-and-continue
    /// (not withheld). Filtering findings by "is this tool's name in the withheld set at all" would
    /// wrongly sweep that unrelated withhold into the drift-commit exclusion and freeze the tool's
    /// baseline forever; only a drift finding that was itself withheld may block its own commit.
    /// </remarks>
    private (HashSet<string> WithheldNames, HashSet<McpSurfaceToolReference> WithheldDriftTools) ApplySurfaceFindings(
        IReadOnlyList<McpSurfaceFinding> findings)
    {
        var withheld = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var withheldDrift = new HashSet<McpSurfaceToolReference>(McpSurfaceToolReference.CaseInsensitiveComparer);
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
                    var driftWithheld = strictDrift && finding.Severity >= threshold;
                    LogAndMaybeWithhold(finding, driftWithheld, withheld);
                    if (driftWithheld)
                        withheldDrift.Add(finding.InvolvedTools[0]);
                    break;
            }
        }

        return (withheld, withheldDrift);
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
