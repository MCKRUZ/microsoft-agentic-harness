using System.Text.RegularExpressions;
using Application.AI.Common.Interfaces;
using Domain.Common.Config.MetaHarness;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Security;

/// <summary>
/// Regex-based secret redactor that scans free-text strings for known secret patterns
/// and filters config keys by a configurable denylist.
/// </summary>
/// <remarks>
/// <para>
/// Patterns are compiled once at construction time from <see cref="MetaHarnessConfig.SecretsRedactionPatterns"/>
/// and the hardcoded free-text regex set. Config changes are not reflected at runtime — restart
/// the service to pick up updated denylist patterns.
/// </para>
/// <para>
/// All methods are thread-safe: compiled <see cref="Regex"/> instances are stateless after
/// construction, and the denylist is an immutable snapshot captured at startup.
/// </para>
/// </remarks>
public sealed class PatternSecretRedactor : ISecretRedactor
{
    private readonly IReadOnlyList<string> _denylistPatterns;
    private readonly IReadOnlyList<(Regex Pattern, string Replacement)> _redactionPatterns;

    /// <summary>
    /// Initializes a new instance using the meta-harness configuration for the denylist.
    /// </summary>
    /// <param name="config">
    /// The meta-harness configuration monitor. Only <see cref="MetaHarnessConfig.SecretsRedactionPatterns"/>
    /// is read, and only at construction time. Changes to the config after startup are ignored.
    /// </param>
    public PatternSecretRedactor(IOptionsMonitor<MetaHarnessConfig> config)
        : this(config.CurrentValue.SecretsRedactionPatterns ?? [])
    {
    }

    /// <summary>
    /// Initializes a new instance with an explicit denylist. Intended for testing.
    /// </summary>
    /// <param name="denylistPatterns">
    /// Case-insensitive substrings matched against config key names by <see cref="IsSecretKey"/>.
    /// Must not be null; pass an empty list to disable key filtering.
    /// </param>
    public PatternSecretRedactor(IReadOnlyList<string> denylistPatterns)
    {
        _denylistPatterns = denylistPatterns;
        _redactionPatterns = Array.AsReadOnly(BuildRedactionPatterns());
    }

    /// <inheritdoc />
    public string? Redact(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        var result = input;
        foreach (var (pattern, replacement) in _redactionPatterns)
            result = pattern.Replace(result, replacement);

        return result;
    }

    /// <inheritdoc />
    public bool IsSecretKey(string configKey)
    {
        if (string.IsNullOrEmpty(configKey))
            return false;

        foreach (var pattern in _denylistPatterns)
        {
            if (configKey.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static (Regex Pattern, string Replacement)[] BuildRedactionPatterns() =>
    [
        // Bearer tokens: case-insensitive; replaces entire match preserving the "Bearer" prefix.
        // The character class [A-Za-z0-9\-._~+/] excludes '[' and ']', so "[REDACTED]" is immune.
        (
            new Regex(
                @"Bearer\s+[A-Za-z0-9\-._~+/]+=*",
                RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "Bearer [REDACTED]"
        ),

        // Connection string value segments: AccountKey=..., Password=..., etc.
        // Negative lookahead (?!\[REDACTED\]) ensures idempotency — already-redacted values
        // are not re-matched on a second Redact() call.
        (
            new Regex(
                @"(?i)(AccountKey|Password|pwd|SharedAccessKey)\s*=\s*(?!\[REDACTED\])[^;""'\s]+",
                RegexOptions.Compiled),
            "$1=[REDACTED]"
        ),

        // Generic key=value / key:value secret pairs.
        // Negative lookahead (?!\[REDACTED\]) ensures idempotency.
        (
            new Regex(
                @"(?i)(api[_-]?key|access[_-]?token|secret[_-]?key)\s*[=:]\s*(?!\[REDACTED\])\S+",
                RegexOptions.Compiled),
            "$1=[REDACTED]"
        ),
    ];
}
