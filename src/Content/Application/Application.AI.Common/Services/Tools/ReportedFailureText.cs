namespace Application.AI.Common.Services.Tools;

/// <summary>
/// Bounds a failure message before it is redacted and reported, so a hostile or malfunctioning tool
/// source cannot make the audit trail pay for an unbounded regex sweep and an unbounded persisted
/// record.
/// </summary>
/// <remarks>
/// Shared by <see cref="GovernedAIFunction"/> and <see cref="DirectToolInvoker"/> — both reporting
/// chokepoints face the same threat model: an MCP server (or any other tool source this process does
/// not control) can return arbitrarily large failure text, which <see cref="Interfaces.Telemetry.IContentRedactionFilter"/>
/// would otherwise run its full rule set over, and which <c>EscalationExecutionRecord.FailureReason</c>
/// would otherwise persist without limit.
/// </remarks>
internal static class ReportedFailureText
{
    private const int MaxLength = 4096;

    /// <summary>Truncates <paramref name="text"/> to a bounded length, if it exceeds one.</summary>
    public static string Cap(string text) =>
        text.Length > MaxLength ? string.Concat(text.AsSpan(0, MaxLength), "…[truncated]") : text;
}
