using Domain.AI.Telemetry.Redaction;

namespace Application.AI.Common.Interfaces.Telemetry;

/// <summary>
/// Redacts PII / secret content from free-text before it is attached to a
/// telemetry span attribute. Implementations are pure functions of their
/// input and MUST be thread-safe — span emission runs on whatever thread
/// happens to own the activity.
/// </summary>
/// <remarks>
/// <para>
/// The harness applies the filter at the boundary between domain content and
/// span emission. Callers pass the raw content string, the categories they
/// want redacted (typically derived from <c>AppConfig.AI.Telemetry.ContentCapture</c>
/// or fixed by policy), and receive the sanitised string ready for attribute
/// assignment.
/// </para>
/// <para>
/// Implementations SHOULD favour conservative (over-redacting) matches: a
/// false positive that hides a string segment is acceptable, a false negative
/// that lets a credit-card number into a span attribute is not.
/// </para>
/// </remarks>
public interface IContentRedactionFilter
{
    /// <summary>
    /// Returns <paramref name="content"/> with every match of every requested
    /// category replaced by the category's configured placeholder. Returns
    /// the input unchanged when it is null, empty, or contains no matches.
    /// </summary>
    /// <param name="content">Raw content captured from the domain.</param>
    /// <param name="categories">
    /// Categories to redact in this call. Pass an empty array to disable
    /// every rule (the filter returns the input unchanged).
    /// </param>
    /// <returns>The redacted content string.</returns>
    string Redact(string? content, IReadOnlyList<RedactionCategory> categories);
}
