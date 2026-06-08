namespace Domain.AI.Telemetry.Redaction;

/// <summary>
/// A single regex-driven redaction rule. Rules are paired with a
/// <see cref="RedactionCategory"/> so a content filter can opt in or out per
/// category without rebuilding the whole pattern list.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Pattern"/> is interpreted as a .NET regular expression. The
/// content filter compiles each pattern once at construction and re-uses the
/// compiled instance; runtime mutation is not supported.
/// </para>
/// <para>
/// <see cref="Replacement"/> may include substitution groups (<c>$1</c>, etc.)
/// when the rule wants to preserve a label (e.g. <c>"Bearer [REDACTED]"</c>).
/// Conservative rules — favouring over-redaction — are correct here: false
/// positives are acceptable, false negatives (PII leaking into a span) are
/// not.
/// </para>
/// </remarks>
/// <param name="Category">PII / secret category this rule belongs to.</param>
/// <param name="Pattern">.NET regular-expression pattern matched against content.</param>
/// <param name="Replacement">Replacement string applied to every match.</param>
public sealed record RedactionRule(
    RedactionCategory Category,
    string Pattern,
    string Replacement);
