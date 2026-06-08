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
