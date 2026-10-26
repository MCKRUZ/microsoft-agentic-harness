using System.Text.RegularExpressions;
using Application.AI.Common.Interfaces.Telemetry;
using Domain.AI.Telemetry.Redaction;

namespace Infrastructure.AI.Audit;

/// <summary>
/// Redacts free text bound for a hash-chained audit trail (#903) without touching the audit references already in it.
/// </summary>
/// <remarks>
/// A producer may have put a person's reference or a fingerprint into the text (<c>By:ref:{key id}:{hex}</c>). Their hex
/// is exactly what the digit rules take for a phone number or a card, and a mangled reference matches nobody. So each
/// reference is swapped for a marker the rules cannot match, the text redacted, and the references put back.
/// </remarks>
public static partial class AuditText
{
    private const char MarkerStart = '';
    private const char MarkerEnd = '';

    /// <summary>Returns <paramref name="text"/> with every redaction category applied and its audit references intact.</summary>
    /// <param name="text">Free text for an audit record.</param>
    /// <param name="redaction">The redaction filter.</param>
    /// <param name="keyId">
    /// The id of the key in use. Only references made under it are shielded: anything else shaped like one is ordinary
    /// text, and redacted as such, so the shield cannot be used to slip a value past the filter.
    /// </param>
    /// <returns>The redacted text.</returns>
    public static string Redact(string text, IContentRedactionFilter redaction, string keyId)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(redaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);

        // A marker character already in the text would be read back as one of ours; it means nothing, so drop it.
        var cleaned = text.Replace(MarkerStart.ToString(), string.Empty, StringComparison.Ordinal)
            .Replace(MarkerEnd.ToString(), string.Empty, StringComparison.Ordinal);
        var references = new List<string>();
        var marked = Reference().Replace(cleaned, match =>
        {
            if (!string.Equals(match.Groups["key"].Value, keyId, StringComparison.Ordinal))
                return match.Value;
            references.Add(match.Value);
            return $"{MarkerStart}{ToLetters(references.Count - 1)}{MarkerEnd}";
        });

        var redacted = redaction.Redact(marked, RedactionCategories.All);
        for (var i = 0; i < references.Count; i++)
            redacted = redacted.Replace($"{MarkerStart}{ToLetters(i)}{MarkerEnd}", references[i], StringComparison.Ordinal);
        return redacted;
    }

    // Letters inside private-use characters: no redaction rule matches a letter run, and nothing real contains them.
    private static string ToLetters(int index) =>
        index < 26 ? ((char)('a' + index)).ToString() : ToLetters((index / 26) - 1) + (char)('a' + (index % 26));

    [GeneratedRegex(@"\b(?:ref|fp):(?<key>[0-9a-f]{8}):[0-9a-f]{64}\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Reference();
}
