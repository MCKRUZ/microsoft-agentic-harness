namespace Domain.Common.Helpers;

/// <summary>
/// Base64URL (RFC 4648 §5) encoder. URL-safe alphabet (<c>+ → -</c>, <c>/ → _</c>)
/// with padding stripped, so the output is filename-safe and shorter than hex.
/// </summary>
/// <remarks>
/// Used for content-addressed identifiers where the encoded value flows through
/// paths, URLs, or other contexts that disallow the standard Base64 alphabet.
/// Encoding is one-way for the callers in this codebase (id derivation, evidence
/// hashing) so no decoder is provided — add one only if a consumer actually needs
/// it, to keep the surface honest.
/// </remarks>
public static class Base64UrlHelper
{
    /// <summary>
    /// Encode <paramref name="bytes"/> as a Base64URL string with no padding.
    /// </summary>
    /// <param name="bytes">Raw bytes to encode.</param>
    /// <returns>
    /// Base64URL-encoded string. For a 32-byte SHA-256 hash this is 43 characters.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="bytes"/> is null.</exception>
    public static string Encode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return Encode(bytes.AsSpan());
    }

    /// <summary>
    /// Encode <paramref name="bytes"/> as a Base64URL string with no padding.
    /// </summary>
    /// <param name="bytes">Raw bytes to encode.</param>
    /// <returns>Base64URL-encoded string, no padding.</returns>
    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        var standard = Convert.ToBase64String(bytes);
        // Base64URL: + → -, / → _, drop = padding.
        return standard.Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
