using System.Security.Cryptography;
using System.Text;
using Application.AI.Common.Interfaces.Compliance;
using Domain.Common.Config.AI;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.Services.Compliance;

/// <summary>
/// HMAC-SHA256 of the subject's id under <c>AppConfig:AI:Erasure:SubjectReferenceKey</c>. Anyone holding the key can
/// recompute a person's reference and find their erasure records; no one without it can reverse one, which a plain hash of
/// a guessable id would not prevent.
/// </summary>
/// <remarks>
/// The key is read on every call so a rotated key takes effect without a restart, and is held to
/// <see cref="IsUsableKey"/> on every call too: <c>ErasureConfigValidator</c> checks it at startup and on reload through
/// the options pipeline, and this check stands in for a host that binds the section some other way. A key that is set but
/// unusable throws rather than producing a weak reference; the caller records the reference as unavailable and erases
/// regardless.
/// </remarks>
public sealed class HmacErasureSubjectReference : IErasureSubjectReference
{
    /// <summary>The shortest key accepted, in bytes: the output size of HMAC-SHA256, so the key is never the weak part.</summary>
    public const int MinimumKeyBytes = 32;

    private readonly IOptionsMonitor<ErasureConfig> _config;

    /// <summary>Initializes a new instance of the <see cref="HmacErasureSubjectReference"/> class.</summary>
    /// <param name="config">Erasure configuration holding the reference key.</param>
    public HmacErasureSubjectReference(IOptionsMonitor<ErasureConfig> config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
    }

    /// <summary>Whether <paramref name="key"/> is base64 of at least <see cref="MinimumKeyBytes"/> bytes.</summary>
    /// <param name="key">The configured key.</param>
    /// <returns><see langword="true"/> when the key is usable.</returns>
    public static bool IsUsableKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var buffer = new byte[key.Length];
        try
        {
            return Convert.TryFromBase64String(key, buffer, out var written) && written >= MinimumKeyBytes;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    /// <inheritdoc />
    public string? For(string subjectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);

        var encodedKey = _config.CurrentValue.SubjectReferenceKey;
        if (string.IsNullOrEmpty(encodedKey))
            return null;

        if (!IsUsableKey(encodedKey))
            throw new InvalidOperationException(
                $"The erasure subject-reference key is not base64 of at least {MinimumKeyBytes} bytes.");

        var key = Convert.FromBase64String(encodedKey);
        try
        {
            return Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(subjectId)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
