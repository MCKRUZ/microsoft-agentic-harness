namespace Domain.AI.Escalation;

/// <summary>
/// Why a reported failure's text is a substitute rather than the tool's own message — carried
/// alongside the text itself (#472) so a downstream consumer (the audit trail, the approver card
/// replaying "why it failed last time") can render a distinct badge instead of trying to tell a
/// substitution apart from a tool that genuinely emitted matching text. Domain-level rather than
/// living next to the sanitizer that produces it, because <see cref="EscalationExecutionRecord"/>
/// needs to carry it and Domain must not depend on Application.
/// </summary>
public enum FailureTextSubstitution
{
    /// <summary>The tool's own text, sanitized/redacted/capped — not a substitution.</summary>
    None = 0,

    /// <summary>The input exceeded the scan-cost bound before sanitize/redact ever ran.</summary>
    Oversized,

    /// <summary>Sanitization removed all content from the tool's failure message.</summary>
    SanitizedToEmpty,

    /// <summary>Sanitizing or redacting the text threw; the raw text is withheld rather than leaked unsafely.</summary>
    TreatmentFailed,
}
