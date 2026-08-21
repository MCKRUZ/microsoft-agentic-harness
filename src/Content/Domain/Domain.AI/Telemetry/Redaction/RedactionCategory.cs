using System.Collections.Immutable;

namespace Domain.AI.Telemetry.Redaction;

/// <summary>
/// PII / secret categories the harness recognises and can redact from
/// telemetry content before it is attached to a span. Each value names a
/// detection rule; <see cref="Generic"/> is the catch-all for harness-vendored
/// patterns that do not map to a regulated PII class.
/// </summary>
/// <remarks>
/// Order is not significant — the redactor applies all configured categories
/// in a single pass. New categories are added at the end of the enum so the
/// underlying integer values remain stable for any consumer that persists
/// them.
/// </remarks>
public enum RedactionCategory
{
    /// <summary>Email addresses (RFC 5322 simplified).</summary>
    Email = 0,

    /// <summary>Phone numbers (E.164 + common North-American formats).</summary>
    Phone = 1,

    /// <summary>US Social Security Numbers.</summary>
    Ssn = 2,

    /// <summary>Credit-card primary account numbers (PAN) — broad Luhn-ish pattern.</summary>
    CreditCard = 3,

    /// <summary>IPv4 and IPv6 addresses.</summary>
    IpAddress = 4,

    /// <summary>AWS access-key identifiers (<c>AKIA…</c>, <c>ASIA…</c>) and similar.</summary>
    AwsKey = 5,

    /// <summary>JWT tokens (header.payload.signature with base64url segments).</summary>
    JwtToken = 6,

    /// <summary>Catch-all bucket for harness-vendored generic secret patterns.</summary>
    Generic = 7,
}

/// <summary>
/// The canonical "every category" source for a caller that redacts unconditionally rather than
/// against a configured subset.
/// </summary>
/// <remarks>
/// Before this existed, every unconditional-redaction call site declared its own private
/// <c>Enum.GetValues&lt;RedactionCategory&gt;()</c> array with a near-identical justification
/// comment — four independent copies across the log, span, and tool-reporting redaction paths.
/// A category added to the enum only needs this one array updated implicitly (it's computed, not
/// enumerated by hand) for every caller that references it to pick it up.
/// </remarks>
public static class RedactionCategories
{
    /// <summary>
    /// Every <see cref="RedactionCategory"/> value. Use where redaction must always run in full —
    /// the audit trail, escalation memory, and AG-UI stream a failed tool call's error text reaches,
    /// and the OTLP trace exporter a recorded exception reaches — none of which are optional
    /// telemetry a consumer can choose to leave unredacted.
    /// </summary>
    public static readonly ImmutableArray<RedactionCategory> All = [.. Enum.GetValues<RedactionCategory>()];
}
