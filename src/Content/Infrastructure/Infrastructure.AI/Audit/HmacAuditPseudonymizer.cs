using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Application.AI.Common.Interfaces.Audit;
using Application.Common.Interfaces.Common;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Audit;

/// <summary>
/// The <see cref="IAuditPseudonymizer"/>: HMAC-SHA256 under keys derived with HKDF (SHA-256) from the one audit pseudonym
/// key, a key for person references and one per fingerprint purpose (#903).
/// </summary>
/// <remarks>
/// <para>
/// HKDF-Extract runs first, with a fixed salt, so a configured key that is long but not uniformly random (a passphrase
/// turned into base64) still yields uniform subkeys; a made key is uniform already and loses nothing by it.
/// </para>
/// <para>
/// Registered as a singleton and resolved at startup by <see cref="AuditPseudonymKeyStartup"/>, so an unusable key or a
/// key file that cannot be written stops the host at boot instead of failing the first audit write.
/// </para>
/// </remarks>
public sealed class HmacAuditPseudonymizer : IAuditPseudonymizer
{
    private const string PersonInfo = "audit-person";
    private const string FingerprintInfoPrefix = "audit-fingerprint:";
    private const string KeyIdInfo = "audit-key-id";
    private static readonly byte[] ExtractSalt = "agentic-harness-audit-pseudonym-v1"u8.ToArray();

    private readonly byte[] _key;
    private readonly byte[] _personKey;
    private readonly ConcurrentDictionary<string, byte[]> _purposeKeys = new(StringComparer.Ordinal);

    // The tokens this instance made, so free text redaction can tell a producer's reference from text shaped like one.
    // Bounded: a producer writes its record moments after making the reference, so only recent tokens need remembering.
    private const int IssuedCapacity = 16384;
    private readonly ConcurrentDictionary<string, byte> _issued = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _issuedOrder = new();

    /// <summary>Initializes a new instance of the <see cref="HmacAuditPseudonymizer"/> class.</summary>
    /// <param name="config">Application configuration holding the audit pseudonym key settings.</param>
    /// <param name="directories">Creates the key file's folder with owner-only access.</param>
    /// <param name="logger">Logger for the warning written when a key file is made.</param>
    /// <exception cref="InvalidOperationException">The configured key or the key file's content is not a usable key.</exception>
    public HmacAuditPseudonymizer(
        IOptions<AppConfig> config,
        IOwnerOnlyDirectoryCreator directories,
        ILogger<HmacAuditPseudonymizer> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(directories);
        ArgumentNullException.ThrowIfNull(logger);

        var key = AuditPseudonymKey.Resolve(config.Value, directories, logger);
        _key = HKDF.Extract(HashAlgorithmName.SHA256, key, ExtractSalt);
        CryptographicOperations.ZeroMemory(key);
        _personKey = Derive(PersonInfo);
        KeyId = Convert.ToHexStringLower(Derive(KeyIdInfo).AsSpan(0, 4));
    }

    /// <inheritdoc />
    public string KeyId { get; }

    /// <inheritdoc />
    public string Person(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        return Remember($"ref:{KeyId}:{Hash(_personKey, identifier.ToUpperInvariant())}");
    }

    /// <inheritdoc />
    public string Fingerprint(string purpose, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentNullException.ThrowIfNull(value);
        var key = _purposeKeys.GetOrAdd(purpose, p => Derive(FingerprintInfoPrefix + p));
        return Remember($"fp:{KeyId}:{Hash(key, value)}");
    }

    /// <inheritdoc />
    public bool Issued(string token) => token is not null && _issued.ContainsKey(token);

    private string Remember(string token)
    {
        if (_issued.TryAdd(token, 0))
        {
            _issuedOrder.Enqueue(token);
            while (_issuedOrder.Count > IssuedCapacity && _issuedOrder.TryDequeue(out var oldest))
                _issued.TryRemove(oldest, out _);
        }

        return token;
    }

    private static string Hash(byte[] key, string value) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value)));

    private byte[] Derive(string info) =>
        HKDF.Expand(HashAlgorithmName.SHA256, _key, AuditPseudonymKey.KeyBytes, Encoding.UTF8.GetBytes(info));
}
