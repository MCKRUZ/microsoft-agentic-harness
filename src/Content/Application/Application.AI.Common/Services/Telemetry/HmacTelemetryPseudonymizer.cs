using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Application.AI.Common.Interfaces.Telemetry;
using Application.AI.Common.Services.Compliance;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.Services.Telemetry;

/// <summary>
/// The single <see cref="ITelemetryPseudonymizer"/>: HMAC-SHA256 under one key resolved once for the life of the process
/// (#903). Registered as a singleton, so the PII span filter, the tool-argument fingerprint and the MCP audit log line all
/// hash under the same key and the same value gives the same pseudonym wherever it appears.
/// </summary>
/// <remarks>
/// <para>
/// <b>The key</b> is <c>Observability:PiiFiltering:HmacKey</c>, base64 of at least
/// <see cref="HmacErasureSubjectReference.MinimumKeyBytes"/> bytes, the same rule the erasure subject reference applies.
/// A key that is set but malformed is refused in every environment: at startup by <c>PiiFilteringConfigValidator</c>, and
/// here at construction for a host that binds the section some other way. It is a configuration mistake, and hashing on
/// with a key nobody meant would quietly change every pseudonym.
/// </para>
/// <para>
/// <b>With no key</b> a random one is made once at startup and a warning says so. The pseudonyms stay private, since
/// nobody holds the key, but they change on restart and differ between instances. This is deliberate rather than a
/// refusal to start: the attribute filter is on by default, so a refusal would stop every host that runs outside
/// Development without a key, including the container and Foundry images, for no gain in privacy.
/// </para>
/// </remarks>
public sealed class HmacTelemetryPseudonymizer : ITelemetryPseudonymizer
{
    private readonly byte[] _key;
    private readonly ConcurrentDictionary<string, byte[]> _purposeKeys = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="HmacTelemetryPseudonymizer"/> class.</summary>
    /// <param name="appConfig">Application configuration holding the telemetry key.</param>
    /// <param name="logger">Logger for the startup warning when no key is configured.</param>
    /// <exception cref="InvalidOperationException">A key is configured but is not base64 of at least 32 bytes.</exception>
    public HmacTelemetryPseudonymizer(IOptions<AppConfig> appConfig, ILogger<HmacTelemetryPseudonymizer> logger)
    {
        ArgumentNullException.ThrowIfNull(appConfig);
        ArgumentNullException.ThrowIfNull(logger);

        var configured = appConfig.Value.Observability.PiiFiltering.HmacKey;
        if (string.IsNullOrEmpty(configured))
        {
            _key = RandomNumberGenerator.GetBytes(HmacErasureSubjectReference.MinimumKeyBytes);
            logger.LogWarning(
                "No Observability:PiiFiltering:HmacKey is configured: telemetry pseudonyms use a random key made at " +
                "startup, so they change on restart and differ between instances. Configure the key where per-person " +
                "counts must add up across them.");
            return;
        }

        if (!HmacErasureSubjectReference.IsUsableKey(configured))
        {
            throw new InvalidOperationException(
                $"Observability:PiiFiltering:HmacKey is set but is not base64 of at least {HmacErasureSubjectReference.MinimumKeyBytes} bytes.");
        }

        _key = Convert.FromBase64String(configured);
    }

    /// <inheritdoc />
    public string HashAttribute(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(value)));
    }

    /// <inheritdoc />
    public string Fingerprint(string purpose, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentNullException.ThrowIfNull(value);

        var purposeKey = _purposeKeys.GetOrAdd(purpose, DerivePurposeKey);
        return Convert.ToHexStringLower(HMACSHA256.HashData(purposeKey, Encoding.UTF8.GetBytes(value)));
    }

    // A key per purpose rather than a purpose prefix on the input: with a prefix, an attribute hash of
    // "purpose\0value" would equal the fingerprint of "value", so whoever controls a hashed attribute could compute any
    // purpose's fingerprint of a guess.
    private byte[] DerivePurposeKey(string purpose) =>
        HKDF.Expand(HashAlgorithmName.SHA256, _key, HmacErasureSubjectReference.MinimumKeyBytes, Encoding.UTF8.GetBytes(purpose));
}
