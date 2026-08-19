using System.Diagnostics;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Governance;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Governance;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// Reports a <see cref="ToolCompositionAssessment"/> through the three existing governance telemetry
/// channels — audit, metrics, structured log — so the three build-time call sites that stamp
/// composition findings (<c>ToolChainBuilder.BuildToolsAsync</c>,
/// <c>BuildMergedToolsWithSourcesAsync</c>, and <c>BuildToolsByName</c>) cannot report differently.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No new side channel.</strong> The acceptance criteria for this feature require findings to
/// be "visible through existing governance metrics/telemetry rather than a new side channel". This type
/// exists only to make sure both call sites use those existing channels identically — it is not itself
/// a fourth channel.
/// </para>
/// <para>
/// <strong>Why not the turn-scoped <see cref="IGovernanceTraceRecorder"/>.</strong> That recorder is
/// scoped to one agent turn and reset by the admission chain at the start of each turn — see its own
/// XML doc. A finding computed at agent <em>build</em> time, before any turn exists, would be recorded
/// into whichever turn's scope happens to be active, or wiped by the first turn's own reset. Audit and
/// metrics are both process-scoped and carry no such lifetime mismatch.
/// </para>
/// </remarks>
public sealed class ToolCompositionReporter
{
    private readonly IGovernanceAuditService _auditService;
    private readonly IOptionsMonitor<GovernanceConfig> _governanceConfig;
    private readonly ILogger<ToolCompositionReporter> _logger;

    /// <summary>Initializes a new instance of the <see cref="ToolCompositionReporter"/> class.</summary>
    public ToolCompositionReporter(
        IGovernanceAuditService auditService,
        IOptionsMonitor<GovernanceConfig> governanceConfig,
        ILogger<ToolCompositionReporter> logger)
    {
        ArgumentNullException.ThrowIfNull(auditService);
        ArgumentNullException.ThrowIfNull(governanceConfig);
        ArgumentNullException.ThrowIfNull(logger);

        _auditService = auditService;
        _governanceConfig = governanceConfig;
        _logger = logger;
    }

    /// <summary>
    /// Reports every finding and the unclassified-tool count in <paramref name="assessment"/>.
    /// A no-op assessment (no findings, no unclassified tools) reports nothing at all — the common case
    /// for a host that has not configured any pairing above <see cref="CompositionPosture.Allow"/>.
    /// </summary>
    /// <param name="agentName">
    /// The agent whose tool set produced this assessment. Named parameter deliberately called
    /// <c>agentName</c> rather than <c>agentId</c>: <see cref="IGovernanceAuditService.Log"/>'s
    /// parameter is named <c>agentId</c>, but the caller — <c>ToolChainBuilder</c>, running inside
    /// <c>AgentExecutionContextFactory.MapToAgentContextAsync</c> — resolves its own tool set before the
    /// agent's id is assigned, so the agent's name is the only stable identifier in scope. Documented
    /// here rather than silently substituted.
    /// </param>
    public void Report(string agentName, ToolCompositionAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);

        if (assessment.Findings.Count == 0 && assessment.UnclassifiedTools.Count == 0)
            return;

        var governance = _governanceConfig.CurrentValue;
        var gating = governance.ToolCompositionGating;

        foreach (var finding in assessment.Findings)
        {
            // Posture is resolved live, here, against the current config — never trusted from the
            // finding, which carries none. See ToolCompositionFinding's remarks: a finding is a
            // structural fact, and under the default configuration (every pairing Allow) this loop
            // reports nothing at all, which is what keeps the feature inert by default even though
            // Findings itself is populated with every co-resident pairing regardless of posture.
            var posture = ToolCompositionPostureResolver.Resolve(gating, finding.SourceCapability, finding.SinkCapability);
            if (posture == CompositionPosture.Allow)
                continue;

            var path = finding.Path;

            var tags = new TagList
            {
                { GovernanceConventions.CompositionSourceCapabilityTag, finding.SourceCapability.ToString() },
                { GovernanceConventions.CompositionSinkCapabilityTag, finding.SinkCapability.ToString() },
                { GovernanceConventions.CompositionPostureTag, posture.ToString() },
            };
            GovernanceMetrics.ToolCompositionFindings.Add(1, tags);

            _logger.LogWarning(
                "Tool composition finding for agent {AgentName}: {Path} (posture: {Posture})",
                agentName, path, posture);

            _auditService.LogIfAuditEnabled(governance, agentName, $"tool_composition:{path}", posture.ToString());
        }

        if (assessment.UnclassifiedTools.Count > 0)
        {
            GovernanceMetrics.ToolCompositionUnclassified.Add(assessment.UnclassifiedTools.Count);

            // Unclassified is the expected common case (ToolCapabilityKeywordRules' own remarks: the
            // vocabulary is deliberately narrow, so most third-party tools go unclassified) — this
            // guard is what keeps the join from running, and being thrown away, on almost every build.
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Tool composition analysis for agent {AgentName}: {Count} tool(s) unclassified — {Tools}",
                    agentName, assessment.UnclassifiedTools.Count, string.Join(", ", assessment.UnclassifiedTools));
            }
        }

        if (assessment.Truncated)
        {
            _logger.LogWarning(
                "Tool composition analysis for agent {AgentName}: finding count truncated at the cap — " +
                "the tool set has more source/sink pairings than were reported",
                agentName);
        }
    }
}
