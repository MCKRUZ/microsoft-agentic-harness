using System.Security.Cryptography;
using Application.AI.Common.Interfaces.Changes;
using Domain.Common.Config;
using Domain.Common.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Changes;

/// <summary>
/// File-system-backed content-addressed <see cref="IEvidenceStore"/>. Stores
/// each blob as <c>{root}/{hash-prefix}/{hash}.bin</c> with a sidecar
/// <c>.contenttype</c> recording the original content type. Two-character
/// fan-out keeps directory entry counts manageable as evidence grows.
/// </summary>
/// <remarks>
/// Reads use <c>FileShare.Read</c> so concurrent readers don't block; writes use
/// <c>File.Exists</c>-then-write because content addressing makes the write
/// pure (same content → same path). The race window is benign: two
/// simultaneous writes of the same content both produce a file with the same
/// bytes at the same path.
/// </remarks>
public sealed class FileSystemEvidenceStore : IEvidenceStore
{
    private const string HashPrefix = "sha256:";

    private readonly string _root;
    private readonly ILogger<FileSystemEvidenceStore> _logger;

    /// <summary>Initializes a new <see cref="FileSystemEvidenceStore"/>.</summary>
    public FileSystemEvidenceStore(
        IOptionsMonitor<AppConfig> config,
        ILogger<FileSystemEvidenceStore> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _root = config.CurrentValue.AI.Changes.EvidenceStoragePath;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string> StoreAsync(
        ReadOnlyMemory<byte> content,
        string contentType,
        CancellationToken cancellationToken)
    {
        var hashBytes = SHA256.HashData(content.Span);
        var hash = HashPrefix + Base64UrlHelper.Encode(hashBytes);
        var path = PathFor(hash);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (!File.Exists(path))
        {
            await using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read);
            await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(path + ".contenttype", contentType ?? string.Empty, cancellationToken).ConfigureAwait(false);
        }

        return hash;
    }

    /// <inheritdoc />
    public async Task<ReadOnlyMemory<byte>?> RetrieveAsync(
        string evidenceHash,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(evidenceHash);

        // Reject malformed hashes silently (treat as not-found) so a caller
        // that passes an attacker-controlled hash never escapes the root.
        if (!TryGetSafePath(evidenceHash, out var path))
        {
            _logger.LogDebug("Evidence hash {Hash} rejected as malformed; treating as not-found.", evidenceHash);
            return null;
        }

        if (!File.Exists(path))
        {
            _logger.LogDebug("Evidence {Hash} not present on disk.", evidenceHash);
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private string PathFor(string evidenceHash)
    {
        if (!TryGetSafePath(evidenceHash, out var path))
        {
            // Should be unreachable on the write path because StoreAsync derives
            // the hash itself, but defense-in-depth: refuse to construct a path
            // that doesn't match the strict format rather than silently writing
            // outside the root.
            throw new ArgumentException(
                $"Evidence hash '{evidenceHash}' does not match the required 'sha256:<43 Base64URL chars>' format.",
                nameof(evidenceHash));
        }
        return path;
    }

    /// <summary>
    /// Strict format check + safe path derivation. Hash MUST be exactly
    /// <c>"sha256:"</c> followed by 43 characters from the Base64URL alphabet
    /// (<c>A-Z</c>, <c>a-z</c>, <c>0-9</c>, <c>-</c>, <c>_</c>). Any other input
    /// — including hashes with path separators, parent-directory tokens, or
    /// characters outside the alphabet — is rejected. After validation the
    /// 2-char fan-out and full hash are both known to be filename-safe, so the
    /// resulting path cannot escape <see cref="_root"/>.
    /// </summary>
    private bool TryGetSafePath(string evidenceHash, out string path)
    {
        path = string.Empty;

        if (!evidenceHash.StartsWith(HashPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var stripped = evidenceHash[HashPrefix.Length..];
        if (stripped.Length != Base64UrlSha256Length)
        {
            return false;
        }

        // Inline alphabet check — avoids regex backtracking and matches the
        // exact output shape of Base64Url(byte[32]).
        for (var i = 0; i < stripped.Length; i++)
        {
            var c = stripped[i];
            var ok = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_';
            if (!ok)
            {
                return false;
            }
        }

        var prefix = stripped[..2];
        path = Path.Combine(_root, prefix, stripped + ".bin");
        return true;
    }

    /// <summary>32 raw bytes → 43 Base64URL chars (no padding). Constant for the format check.</summary>
    private const int Base64UrlSha256Length = 43;
}
