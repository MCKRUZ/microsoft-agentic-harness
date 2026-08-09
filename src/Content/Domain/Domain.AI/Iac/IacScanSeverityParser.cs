using Domain.Common.Helpers;

namespace Domain.AI.Iac;

/// <summary>
/// Parses the configured <c>BlockingSeverity</c> string into the shared
/// <see cref="IacScanSeverity"/> scale, and decides scan pass/fail against it.
/// Lives in the Domain so both the Application-layer config validator and the
/// Infrastructure-layer generators/startup validator agree on what counts as a
/// valid severity and what blocks a proposal.
/// </summary>
public static class IacScanSeverityParser
{
    /// <summary>
    /// Parses a blocking-severity string (case-insensitive) into an
    /// <see cref="IacScanSeverity"/>.
    /// </summary>
    /// <param name="value">The configured severity, e.g. <c>"High"</c>.</param>
    /// <param name="severity">The parsed severity when recognised.</param>
    /// <returns><see langword="true"/> when the value maps to a known severity; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// <para>
    /// Delegates to <see cref="EnumNameHelper.TryParseName{TEnum}(string?, out TEnum)"/> rather than
    /// restating the rule. This method previously implemented two of that helper's four guards —
    /// trim and <see cref="Enum.IsDefined{TEnum}(TEnum)"/> — so <c>"2"</c> and <c>"Low,Critical"</c>
    /// both parsed. <c>"2"</c> resolved to <see cref="IacScanSeverity.High"/>, making a configured
    /// severity mean the same thing whether it was written as a name or as an opaque number, which
    /// is the interchangeability the shared helper exists to refuse. <c>"Low,Critical"</c> is a
    /// bitwise OR landing on a defined member, which <see cref="Enum.IsDefined{TEnum}(TEnum)"/>
    /// cannot distinguish from having named that member outright.
    /// </para>
    /// <para>
    /// This overload is for <em>configuration</em> — the blocking threshold — where refusing is the
    /// safe answer because every caller treats it as a startup or generation failure. A severity
    /// reported by a scanner against a specific finding has the opposite failure direction and must
    /// use <see cref="ParseFindingSeverity"/> instead.
    /// </para>
    /// </remarks>
    public static bool TryParse(string? value, out IacScanSeverity severity)
        => EnumNameHelper.TryParseName(value, out severity);

    /// <summary>
    /// Reads the severity a scanner reported for a specific finding, resolving anything
    /// unrecognisable to <see cref="IacScanSeverity.Critical"/>.
    /// </summary>
    /// <param name="value">The severity as scraped from scanner output.</param>
    /// <returns>The named severity, or <see cref="IacScanSeverity.Critical"/> when it cannot be read.</returns>
    /// <remarks>
    /// <para>
    /// <strong>Why unreadable means Critical rather than "ignore it".</strong> The same parse serves
    /// two callers whose failure directions are opposite. For the configured blocking threshold,
    /// refusing fails closed: the generators return <c>iac.scan.invalid_blocking_severity</c> and
    /// the startup validator throws. For a finding's severity it failed <em>open</em> — the tfsec
    /// parser dropped the entire finding, and the Checkov and ARM-TTK parsers silently kept their
    /// <see cref="IacScanSeverity.Medium"/> default — so a scanner reporting something the harness
    /// could not read made a blocking scan pass quietly. A security scanner saying something we do
    /// not understand is the last thing that should lower a gate.
    /// </para>
    /// <para>
    /// This applies only when a severity was <em>reported and could not be read</em>. A finding with
    /// no severity line at all keeps each parser's existing default, because Checkov emits severities
    /// only when wired to its platform, and treating every unadorned finding as Critical would block
    /// every scan.
    /// </para>
    /// </remarks>
    public static IacScanSeverity ParseFindingSeverity(string? value)
        => TryParse(value, out var severity) ? severity : IacScanSeverity.Critical;

    /// <summary>
    /// Decides whether a scan passes the gate: it passes when no finding is at or
    /// above the configured blocking severity.
    /// </summary>
    /// <param name="findings">The normalised findings across the scanners that ran.</param>
    /// <param name="blocking">The minimum severity that blocks a proposal.</param>
    /// <returns><see langword="true"/> when no finding meets or exceeds <paramref name="blocking"/>.</returns>
    public static bool Passes(IEnumerable<IacScanFinding> findings, IacScanSeverity blocking)
    {
        ArgumentNullException.ThrowIfNull(findings);
        return !findings.Any(f => f.Severity >= blocking);
    }
}
