using System.Globalization;
using System.Text;
using Domain.AI.Compliance;

namespace Application.Core.Compliance;

/// <summary>
/// Renders a <see cref="ComplianceReport"/> as human-readable Markdown — the export format for a
/// reviewer, alongside the report's own natural JSON shape for machine consumption (#696). Pure
/// formatting, no external dependency.
/// </summary>
public static class ComplianceReportMarkdownRenderer
{
    private const string DateFormat = "yyyy-MM-dd HH:mm:ss 'UTC'";

    /// <summary>Renders the given report as a Markdown document.</summary>
    /// <param name="report">The report to render.</param>
    /// <returns>The report as Markdown text.</returns>
    public static string Render(ComplianceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder();

        sb.AppendLine("# Compliance Report");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Generated:** {Format(report.GeneratedAt)} by `{report.GeneratedBy}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Period:** {Format(report.PeriodStart)} to {Format(report.PeriodEnd)}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"- **Scope:** {(report.ConversationId is null ? "All conversations" : $"Conversation `{report.ConversationId}`")}");
        sb.AppendLine();

        if (report.Warnings.Count > 0)
        {
            sb.AppendLine("## ⚠ Warnings — this report is incomplete");
            sb.AppendLine();
            foreach (var warning in report.Warnings)
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {warning}");
            sb.AppendLine();
        }

        sb.AppendLine("## Chain integrity");
        sb.AppendLine();
        sb.AppendLine("| Chain | Status | Records verified | Failure reason |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var chain in report.ChainIntegrity)
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {chain.ChainName} | {(chain.IsValid ? "✅ Intact" : "❌ BROKEN")} | {chain.VerifiedCount} | {chain.FailureReason ?? "—"} |");
        sb.AppendLine();

        sb.AppendLine("## Sessions");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Total: {report.Sessions.TotalSessions}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Completed: {report.Sessions.CompletedSessions}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Errored: {report.Sessions.ErroredSessions}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Cancelled: {report.Sessions.CancelledSessions}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Total cost: ${report.Sessions.TotalCostUsd:F2}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"- Tokens: {report.Sessions.TotalInputTokens:N0} in / {report.Sessions.TotalOutputTokens:N0} out");
        sb.AppendLine();

        sb.AppendLine("## Safety");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Total events: {report.Safety.TotalEvents}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Blocked: {report.Safety.BlockedCount}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Redacted: {report.Safety.RedactedCount}");
        if (report.Safety.CountsByCategory.Count > 0)
        {
            sb.AppendLine("- By category:");
            foreach (var (category, count) in report.Safety.CountsByCategory.OrderByDescending(kv => kv.Value))
                sb.AppendLine(CultureInfo.InvariantCulture, $"  - {category}: {count}");
        }
        sb.AppendLine();

        AppendSection(sb, "Governance decisions", report.GovernanceDecisions,
            r => $"{Format(r.Timestamp)} — agent `{r.AgentId}`, action `{r.Action}`: **{r.Decision}**");
        AppendSection(sb, "Change-proposal decisions", report.ChangeDecisions,
            r => $"{Format(r.Timestamp)} — proposal `{r.ProposalId}`, gate `{r.GateKey}`: **{r.Decision}** ({r.Mode})");
        AppendSection(sb, "Egress decisions", report.EgressDecisions,
            r => $"{Format(r.Timestamp)} — {(r.Allowed ? "ALLOWED" : "DENIED")} {r.Host} ({r.Reason})");
        AppendSection(sb, "Escalation events", report.EscalationEvents,
            r => $"{Format(r.Timestamp)} — escalation `{r.EscalationId}`: {r.RecordType}");
        AppendSection(sb, "Drift findings", report.DriftFindings,
            r => $"{Format(r.RecordedAt)} — event `{r.EventId}`: {r.RecordType}");
        AppendSection(sb, "Audit log entries", report.AuditEntries,
            r => $"{Format(r.CreatedAt)} — [{r.Source}] {r.Operation}");

        return sb.ToString();
    }

    private static void AppendSection<T>(
        StringBuilder sb, string title, IReadOnlyList<T> records, Func<T, string> formatLine)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"## {title} ({records.Count})");
        sb.AppendLine();
        if (records.Count == 0)
        {
            sb.AppendLine("_None in this period._");
        }
        else
        {
            foreach (var record in records)
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {formatLine(record)}");
        }

        sb.AppendLine();
    }

    private static string Format(DateTimeOffset value) =>
        value.UtcDateTime.ToString(DateFormat, CultureInfo.InvariantCulture);
}
