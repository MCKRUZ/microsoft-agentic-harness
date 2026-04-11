namespace Application.AI.Common.Interfaces;

/// <summary>
/// Redacts secrets from free-text strings and filters secret config keys.
/// Applied before any content is persisted to disk (traces, snapshots, manifests).
/// </summary>
/// <remarks>
/// <para>
/// The redactor operates at two boundaries:
/// <list type="number">
/// <item><description>
/// <strong>Config key filtering</strong> — use <see cref="IsSecretKey"/> when building
/// <c>HarnessSnapshot</c> to exclude keys whose names contain a denylist pattern.
/// </description></item>
/// <item><description>
/// <strong>Free-text redaction</strong> — use <see cref="Redact"/> when writing any
/// string artifact to disk to replace recognizable secret shapes with <c>"[REDACTED]"</c>.
/// </description></item>
/// </list>
/// </para>
/// <para>
/// Implementations must be thread-safe and idempotent: applying <see cref="Redact"/>
/// twice to the same input must produce the same output as applying it once.
/// </para>
/// </remarks>
public interface ISecretRedactor
{
    /// <summary>
    /// Scans <paramref name="input"/> for known secret patterns and replaces
    /// matches with <c>"[REDACTED]"</c>. Returns the original string if no patterns match.
    /// Returns <see langword="null"/> or empty unchanged.
    /// </summary>
    /// <param name="input">The string to scan. May be null or empty.</param>
    /// <returns>
    /// The redacted string if any patterns matched; the original <paramref name="input"/>
    /// reference if no patterns matched (no allocation).
    /// </returns>
    string? Redact(string? input);

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="configKey"/> matches any entry in the
    /// secrets denylist and should therefore be excluded from config snapshots.
    /// Matching is case-insensitive substring comparison.
    /// </summary>
    /// <param name="configKey">The configuration key name to evaluate.</param>
    /// <returns>
    /// <see langword="true"/> if the key contains a denylist pattern; otherwise <see langword="false"/>.
    /// </returns>
    bool IsSecretKey(string configKey);
}
