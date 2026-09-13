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
        var mcpCandidates = DeduplicateMcpCandidates(allProvisioned, firstPartyNames, callOnceCandidates);

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
    /// <para>
    /// Grouped by (server, name) together — NOT by name alone, and NOT by a concatenated string key.
    /// Grouping by name alone would collapse two genuinely different servers' same-named tools down to a
    /// single candidate before the surface scanner ever saw more than one of them, silently discarding
    /// the exact collision this scan exists to catch. A concatenated string key has its own version of
    /// the same bug: server "trusted" + tool "reader" and server "trustedread" + tool "er" would hash
    /// identically. A tuple key compares both components independently, so no such collision is
    /// possible. Grouping still only removes true duplicates: the same server's tool recorded twice
    /// because two different resolution paths reached it — including two different skills each
    /// declaring the SAME server/tool pair, which is exactly the case a bare "keep the first" pick
    /// would have silently mishandled.
    /// </para>
    /// <para>
    /// #589 code-review finding (second round): a bare <c>g.First()</c> here has the identical bug
    /// the first-party dedup loop in <see cref="ProjectSurvivors"/> was fixed to avoid — each skill's
    /// own <c>GovernedAIFunction</c> wrapper (with that skill's own <see cref="GovernedAIFunction.SkillIds"/>)
    /// is already built by the time tools from multiple skills are pooled into <paramref name="allProvisioned"/>
    /// here (<c>WrapGoverned</c>, via <c>FinalizeChain</c>, runs per skill upstream of this method), so
    /// two skills naming the same MCP server/tool each produce their own independently-scoped instance.
    /// Folding the group through <see cref="ResolveGroupUnion"/> — the same primitive the
    /// first-party loop uses — closes this the same way, rather than leaving the MCP source exempt from
    /// a fix its own tool-source counterpart already received.
    /// </para>
    /// </remarks>
    private static List<ProvisionedTool> DeduplicateMcpCandidates(
        List<ProvisionedTool> allProvisioned, HashSet<string> firstPartyNames, ConcurrentDictionary<AITool, byte> callOnceCandidates)
        => allProvisioned
            .Where(p => p.McpServerName is not null && !firstPartyNames.Contains(p.Tool.Name))
            .GroupBy(p => (Server: p.McpServerName!.ToUpperInvariant(), Name: p.Tool.Name.ToUpperInvariant()))
            .Select(g => UnionMcpGroup(g, callOnceCandidates))
            .ToList();

    /// <summary>
    /// Computes the full union of every skill's independently-wrapped instance of the same
    /// (server, name) MCP tool in one pass via <see cref="ResolveGroupUnion"/> — the MCP-source
    /// counterpart to <see cref="ProjectSurvivors"/>'s first-party dedup loop (#638).
    /// </summary>
    private static ProvisionedTool UnionMcpGroup(
        IEnumerable<ProvisionedTool> group, ConcurrentDictionary<AITool, byte> callOnceCandidates)
    {
        var instances = group.ToList();
        var unionedTool = ResolveGroupUnion(instances.ConvertAll(p => p.Tool), callOnceCandidates);
        return instances[0] with { Tool = unionedTool };
    }

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
    /// <strong>Two (or more) skills sharing a first-party tool name union every sharing skill's egress
    /// scope (#531, #589, #638).</strong> Each skill's tools are already wrapped as
    /// <see cref="GovernedAIFunction"/> — one instance's <c>SkillIds</c> baked in per skill — before
    /// <paramref name="allProvisioned"/> reaches this method (<c>BuildProvisionedToolsAsync</c>/
    /// <c>FinalizeChain</c> runs per skill, upstream). The first-party dedup loop below groups every
    /// name's contributing instances and, via <see cref="ResolveGroupUnion"/>, computes the FULL union
    /// across the whole group in one pass rather than silently keeping only whichever skill enumerated
    /// first — so a call to the shared tool resolves an egress policy covering every sharing skill's
    /// declared allowlist, regardless of which one the model is conceptually driving the turn from.
    /// <see cref="ResolveGroupUnion"/> also carries <paramref name="callOnceCandidates"/> forward onto
    /// any new instance it constructs — the same alias-preservation <see cref="WrapGoverned"/> already
    /// does — because that set is keyed by reference identity
    /// (<see cref="ReferenceEqualityComparer.Instance"/>): a rewrap that produced a new, never-tagged
    /// instance without this would silently drop a shared tool's <c>CallOncePerConversation</c>
    /// restriction the moment two or more skills happened to share its name (correctness/security
    /// review finding on #589's own first draft).
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

        // #638: grouped, then unioned once per name — see ResolveGroupUnion's remarks for why this
        // replaced a loop that repaired the published instance pairwise on each subsequent same-named
        // tool. GroupBy preserves each group's original relative order and yields groups in order of
        // each key's first appearance in the source, so this produces the identical ordering the old
        // incremental loop did.
        foreach (var group in allProvisioned
            .Where(p => p.McpServerName is null && survivingNames.Contains(p.Tool.Name))
            .GroupBy(p => p.Tool.Name, StringComparer.OrdinalIgnoreCase))
        {
            indexByName[group.Key] = result.Count;
            result.Add(ResolveGroupUnion(group.Select(p => p.Tool).ToList(), callOnceCandidates));
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
    /// Computes the full union of every skill's independently-wrapped instance of the same tool name
    /// in one pass, and wraps at most once with the complete result (#638) — the general form both
    /// <see cref="ProjectSurvivors"/>'s first-party dedup loop and <see cref="UnionMcpGroup"/>'s MCP
    /// dedup loop call, replacing the pairwise incremental repair (<c>UnionSkillScopeIfNeeded</c>/
    /// <c>ResolveUnion</c>) both used before this fix.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why pairwise repair was the wrong shape.</strong> For N skills sharing a tool name, the
    /// old per-name loop unwrapped and re-wrapped the published instance up to N-1 times — once per
    /// additional skill discovered — each rewrap a fresh, independent chance for whichever field list
    /// that rewrap site threads through the <see cref="GovernedAIFunction"/> constructor to be missing
    /// one. That is exactly the shape that dropped call-once candidacy twice across #589's own review
    /// history and <see cref="GovernedAIFunction.SkillIdFromArguments"/> once in #619 — not a specific
    /// bug in any one field's forwarding, but a structural property of "repair after the fact,
    /// incrementally." Computing every field's full union across the WHOLE group up front, then
    /// deciding in one step whether the canonical instance already embodies it, removes the incremental
    /// chain entirely: at most one rewrap happens regardless of how many skills share the name, and
    /// every field this method reads is unioned in the same single pass — there is no longer a
    /// "rewrap site" for a future field addition to forget, because there is only ever one rewrap.
    /// </para>
    /// <para>
    /// <strong>Kept as a belt-and-suspenders check alongside <see cref="GovernedAIFunction.IsCallOnceCandidate"/>
    /// (#621) rather than replaced by it:</strong> <paramref name="callOnceCandidates"/> is consulted
    /// on every ORIGINAL instance in <paramref name="instances"/> (never a synthetic intermediate —
    /// there are none now), because the field only exists on a <see cref="GovernedAIFunction"/>
    /// instance; the dictionary remains the correct mechanism for the (rare) case where an instance is
    /// some other <see cref="AITool"/> shape with no field to carry candidacy on at all. If any
    /// original instance was dictionary-tagged or field-tagged, the returned instance is tagged (both
    /// the field, if a rewrap happens, and the dictionary).
    /// </para>
    /// </remarks>
    /// <param name="instances">
    /// Every skill's own instance of the same-named tool, in original discovery order.
    /// <paramref name="instances"/>[0] is the canonical instance: what's returned unchanged when no
    /// rewrap is needed, and what a rewrap's inner function/<see cref="GovernedAIFunction.CurrentSkillAccessor"/>/
    /// <see cref="GovernedAIFunction.SkillIdFromArguments"/> are drawn from.
    /// </param>
    /// <param name="callOnceCandidates">See this method's remarks.</param>
    /// <returns>
    /// <paramref name="instances"/>[0] unchanged when there is only one instance, the canonical
    /// instance isn't a <see cref="GovernedAIFunction"/>, or it already embodies the full computed
    /// union of every instance's skill ids and call-once candidacy; otherwise a new instance wrapping
    /// the canonical's inner function with that complete union.
    /// </returns>
    private static AITool ResolveGroupUnion(
        IReadOnlyList<AITool> instances, ConcurrentDictionary<AITool, byte> callOnceCandidates)
    {
        var canonical = instances[0];
        if (instances.Count == 1 || canonical is not GovernedAIFunction canonicalGoverned)
            return canonical;

        // Every instance not itself a GovernedAIFunction contributes nothing here (mirrors the old
        // pairwise ResolveUnion's identical early-return for a non-governed side) — in practice this
        // never happens, since WrapGoverned wraps every first-party AIFunction before it reaches this
        // pipeline, but a mixed group degrades to "use whatever the governed members declare" rather
        // than throwing.
        var unionedIds = instances
            .OfType<GovernedAIFunction>()
            .SelectMany(g => g.SkillIds ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var anyCallOnce = instances.Any(t =>
            callOnceCandidates.ContainsKey(t) || (t as GovernedAIFunction)?.IsCallOnceCandidate == true);

        var canonicalIds = canonicalGoverned.SkillIds ?? [];
        var canonicalAlreadyComplete = unionedIds.All(id => canonicalIds.Contains(id, StringComparer.OrdinalIgnoreCase))
            && (!anyCallOnce || canonicalGoverned.IsCallOnceCandidate);

        if (canonicalAlreadyComplete)
            return canonical;

        var result = new GovernedAIFunction(
            canonicalGoverned.Inner, compositionTaint: null, canonicalGoverned.CurrentSkillAccessor,
            unionedIds, canonicalGoverned.SkillIdFromArguments,
            isCallOnceCandidate: anyCallOnce);

        if (anyCallOnce)
            callOnceCandidates.TryAdd(result, 0);

        return result;
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
