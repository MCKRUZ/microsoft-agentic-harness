namespace Application.AI.Common.Services.Tools;

/// <summary>
/// Bounds an already-redacted failure message before it is reported, so an unbounded tool failure
/// cannot make <c>EscalationExecutionRecord.FailureReason</c> pay for an unbounded persisted record.
/// </summary>
/// <remarks>
/// <para>
/// Shared by <see cref="GovernedAIFunction"/> and <see cref="DirectToolInvoker"/> — both reporting
/// chokepoints face the same threat model: an MCP server (or any other tool source this process does
/// not control) can return arbitrarily large failure text, which <c>EscalationExecutionRecord.FailureReason</c>
/// would otherwise persist without limit.
/// </para>
/// <para>
/// Callers MUST call <see cref="Cap"/> on the result of
/// <see cref="Interfaces.Telemetry.IContentRedactionFilter.Redact"/>, never the other way round: capping
/// first can slice a real secret in half at the length boundary, and the truncated fragment that
/// survives is never run back through the redaction filter, so it reaches the audit trail as-is.
/// Redacting the full, uncapped text first and bounding the (already-safe) result afterward is the
/// only ordering that can't leak a fragment.
/// </para>
/// </remarks>
internal static class ReportedFailureText
{
    private const int MaxLength = 4096;

    /// <summary>
    /// Truncates <paramref name="text"/> to a bounded length, if it exceeds one. Never splits a
    /// surrogate pair — a cut that would land inside one backs off by one character instead, so the
    /// result is always a well-formed string.
    /// </summary>
    public static string Cap(string text)
    {
        if (text.Length <= MaxLength)
        {
            return text;
        }

        var cutIndex = MaxLength;
        if (char.IsHighSurrogate(text[cutIndex - 1]))
        {
            cutIndex--;
        }
        return string.Concat(text.AsSpan(0, cutIndex), "…[truncated]");
    }
}
