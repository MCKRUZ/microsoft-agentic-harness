namespace Domain.Common.Helpers;

/// <summary>
/// A tool-call CallId or tool name that has passed through <see cref="ToolCallIdentifierSanitizer.Sanitize"/>
/// (directly, or via the logging wrapper built on it) — distinct from a plain <see cref="string"/> so a
/// caller cannot pass a raw, provider- or model-supplied CallId/ToolName to a consumer that requires an
/// already-sanitized one without a compile error.
/// </summary>
/// <remarks>
/// Before this type existed the discipline was doc-comment-only: every sanitizing caller produced a
/// <c>safeCallId</c>/<c>safeName</c> local and threaded it explicitly into every downstream consumer, but
/// nothing at the type level stopped a future edit — or a new third consumer of
/// <c>FunctionCallContent</c>/<c>FunctionResultContent</c>
/// — from passing the raw <c>.CallId</c>/<c>.Name</c> instead (#633; this repo's own CLAUDE.md "Common
/// Mistakes" names this exact failure shape — a control enforced by convention alone — as having landed
/// 6+ times across unrelated subsystems). No implicit conversion FROM <see cref="string"/> is provided,
/// deliberately: constructing one is possible from any code, but only ever by explicitly naming the type
/// at the construction site, which is what forces a reviewer (or the author) to notice and justify it
/// rather than have a raw string silently widen to fit. The implicit conversion TO <see cref="string"/>
/// exists because, once a value legitimately IS a <see cref="SanitizedIdentifier"/>, treating it as a
/// string everywhere else (log templates, JSON, an interface boundary that still accepts a plain
/// <see cref="string"/>) is safe by construction.
/// </remarks>
public readonly record struct SanitizedIdentifier(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>
    /// Reads a <see cref="SanitizedIdentifier"/> wherever a plain <see cref="string"/> is expected —
    /// safe because the value has already been through <see cref="ToolCallIdentifierSanitizer.Sanitize"/>
    /// by the time one of these exists.
    /// </summary>
    public static implicit operator string(SanitizedIdentifier identifier) => identifier.Value;
}
