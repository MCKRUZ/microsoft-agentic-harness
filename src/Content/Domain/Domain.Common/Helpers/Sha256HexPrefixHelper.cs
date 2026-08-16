using System.Security.Cryptography;
using System.Text;

namespace Domain.Common.Helpers;

/// <summary>
/// Computes a short, deterministic, collision-safe disambiguating suffix for a string value:
/// the leading hex characters of its UTF-8 SHA-256 digest.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists.</strong> Two unrelated naming schemes — <c>BundleOwnedMcpToolNaming</c>
/// (Application.AI.Common) and <c>ScopedCollectionName</c> (Domain.AI.RAG) — independently computed
/// <c>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))</c>, truncated to a
/// caller-chosen length, for the exact same reason: two different raw inputs that collapse to the
/// same value after sanitization/slugification must still produce distinct output. Any future review
/// of either scheme's collision safety had to re-derive the same reasoning twice, in two projects.
/// This extracts only that shared primitive.
/// </para>
/// <para>
/// <strong>What is deliberately NOT shared.</strong> The two callers' cleaning rules are genuinely
/// different by design — one replaces each disallowed character one-for-one with <c>'_'</c>
/// (length-preserving), the other collapses runs of disallowed characters to a single <c>'-'</c> and
/// lowercases/trims. Merging those would blur two policies that exist for different reasons. Only the
/// hash-to-short-hex step is common ground.
/// </para>
/// <para>
/// Length is expressed in hex characters, not bytes: both current callers reason about their budget
/// in characters (a maximum total name length), so asking for hex characters directly avoids a
/// bytes-to-chars conversion at every call site.
/// </para>
/// <para>
/// This lives in <c>Domain.Common</c>, which has no project references of its own, so every layer —
/// Domain, Application, Infrastructure — can reach it without bending a dependency arrow.
/// </para>
/// </remarks>
public static class Sha256HexPrefixHelper
{
    /// <summary>
    /// Computes the leading <paramref name="hexLength"/> hex characters of the SHA-256 digest of
    /// <paramref name="value"/> (interpreted as UTF-8), lowercase.
    /// </summary>
    /// <param name="value">The value to hash.</param>
    /// <param name="hexLength">
    /// Number of leading hex characters to keep. Must be between 1 and 64 (the full digest is 64 hex
    /// characters / 32 bytes).
    /// </param>
    /// <returns>The truncated, lowercase hex digest.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="hexLength"/> is outside 1..64.
    /// </exception>
    public static string Compute(string value, int hexLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(hexLength, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hexLength, 64);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(digest)[..hexLength];
    }
}
