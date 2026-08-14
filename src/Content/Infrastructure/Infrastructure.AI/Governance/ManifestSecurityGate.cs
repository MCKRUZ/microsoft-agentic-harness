using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.Governance;
using Domain.AI.Governance;
using Domain.Common.Config.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Governance;

/// <summary>
/// Shared "read policy, scan, decide, log, throw" sequence for a manifest-loading parser that must
/// refuse a flagged <c>SKILL.md</c>/<c>AGENT.md</c> before constructing its definition type
/// (issue #331). Both <c>SkillMetadataParser</c> and <c>AgentMetadataParser</c> call this rather
/// than each re-deriving the withhold decision, the findings format, and the refusal log/throw —
/// the same duplication risk <c>ScanningMcpToolProvider.IsAdmitted</c> already carries for the
/// admit/withhold half of this decision on MCP tool descriptions.
/// </summary>
internal static class ManifestSecurityGate
{
    /// <summary>
    /// Scans <paramref name="shortFieldsContent"/> (name/description — short, full rule set
    /// including the length-sensitive base64 rule) and each non-empty entry of
    /// <paramref name="longFormContent"/> (instructions/body/tool guidance — long-form prose, the
    /// length-sensitive rules excluded so a legitimate 40+ character token doesn't trip a refusal),
    /// and throws <see cref="ManifestRefusedException"/> if either scan meets
    /// <see cref="GovernanceConfig.McpToolBlockThreshold"/>. No-ops entirely when
    /// <see cref="GovernanceConfig.EnableMcpSecurity"/> is off — the scanner is never called.
    /// </summary>
    /// <param name="scanner">Scans a piece of text for prompt-injection payloads.</param>
    /// <param name="logger">Logs the refusal; never receives the scanned text.</param>
    /// <param name="policy">Supplies the enable flag and block threshold.</param>
    /// <param name="sourceName">Skill/agent id, passed through to the scanner and its result.</param>
    /// <param name="manifestKind">"skill" or "agent" — only used in the refusal log message.</param>
    /// <param name="manifestFilePath">Absolute path of the manifest, carried on the exception.</param>
    /// <param name="shortFieldsContent">Name/description, scanned together.</param>
    /// <param name="longFormContent">Zero or more long-form sections, each scanned separately.</param>
    internal static void ScanOrRefuse(
        IMcpSecurityScanner scanner,
        ILogger logger,
        GovernanceConfig policy,
        string sourceName,
        string manifestKind,
        string manifestFilePath,
        string shortFieldsContent,
        params ReadOnlySpan<string?> longFormContent)
    {
        if (!policy.EnableMcpSecurity)
            return;

        var shortFields = scanner.ScanContent(sourceName, shortFieldsContent, includeLengthSensitiveRules: true);
        var withheld = shortFields.IsWithheld(policy.McpToolBlockThreshold);
        var threats = new List<McpToolThreat>(shortFields.Threats);

        foreach (var content in longFormContent)
        {
            if (string.IsNullOrWhiteSpace(content))
                continue;

            var result = scanner.ScanContent(sourceName, content, includeLengthSensitiveRules: false);
            withheld |= result.IsWithheld(policy.McpToolBlockThreshold);
            threats.AddRange(result.Threats);
        }

        if (!withheld)
            return;

        var findings = threats.ToFindingLabels();
        logger.LogWarning(
            "Refusing {ManifestKind} manifest at {Path}: security scan found {Findings}. Block threshold is {Threshold}.",
            manifestKind, manifestFilePath, string.Join(", ", findings), policy.McpToolBlockThreshold);
        throw new ManifestRefusedException(manifestFilePath, findings);
    }
}
