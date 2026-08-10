namespace Infrastructure.AI.Governance.Adapters;

/// <summary>
/// The character class every invisible-character detector in this folder matches against, shared
/// so <see cref="McpSecurityScannerAdapter"/> (which scans a tool description) and
/// <see cref="ResponseInjectionScrubber"/> (which scans a tool's output) cannot drift apart on the
/// same five characters the way they already had once — the RTL override (U+202E) was added to the
/// description scanner without the output scrubber, and had to be found and fixed separately.
/// </summary>
/// <remarks>
/// <para>
/// Zero-width space (U+200B), zero-width non-joiner (U+200C), the right-to-left override (U+202E),
/// word joiner (U+2060), and the byte-order mark (U+FEFF). U+202E is not invisible — it reverses the
/// display order of the text that follows, so a human reviewing the raw text and the model reading
/// it see two different sentences. That defeats human review rather than evading a pattern, which is
/// why it is treated as Critical alongside the zero-width characters rather than as an ordinary
/// hidden character.
/// </para>
/// <para>
/// <strong>Zero-width joiner (U+200D) is deliberately excluded</strong>, though it is the character
/// most often listed alongside these. It is what builds every compound emoji — 👨‍💻, family and
/// profession sequences, several flags — so scanning for it flags an ordinary emoji as a Critical
/// finding. The joiner also carries no hidden text on its own: it joins visible glyphs rather than
/// separating them, which is the property the other five are abused for.
/// </para>
/// </remarks>
internal static class InvisibleCharacters
{
    /// <summary>
    /// Regex character class matching the invisible/deceptive characters above. Consumed by
    /// <c>[GeneratedRegex]</c>, which requires a compile-time constant.
    /// </summary>
    internal const string Pattern = @"[​‌‮⁠﻿]";
}
