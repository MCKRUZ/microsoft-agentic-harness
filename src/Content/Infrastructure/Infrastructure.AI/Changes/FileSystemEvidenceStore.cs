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
/// Reads use plain <see cref="File.ReadAllBytesAsync(string, CancellationToken)"/>.
/// Writes are <b>atomic</b>: content is streamed to a uniquely-named temporary
/// file in the target directory and then moved into place with
/// <see cref="File.Move(string, string, bool)"/>, which is an atomic rename on
/// the same volume. This guarantees the content-addressed path only ever holds
/// a complete blob — a crash, cancellation, or full disk mid-write leaves an
/// orphan <c>.tmp</c> file rather than a truncated blob at the final path, so
/// the integrity guarantee of the content-addressed store is preserved.
/// Because content addressing makes the write pure (same content → same path),
/// concurrent writes of identical content are idempotent: every writer produces
/// byte-identical content and the final move simply overwrites with the same
/// bytes, honoring the <c>IEvidenceStore</c> idempotency contract without ever
/// throwing.
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

        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        // Already present: content addressing makes the write pure, so an
        // existing blob is byte-identical. Skip rewriting to avoid needless I/O
        // while remaining idempotent.
        if (!File.Exists(path))
        {
            await WriteAtomicAsync(path, content, cancellationToken).ConfigureAwait(false);
            await WriteAtomicTextAsync(path + ".contenttype", contentType ?? string.Empty, cancellationToken).ConfigureAwait(false);
        }

        return hash;
    }

    /// <summary>
    /// Streams <paramref name="content"/> to a uniquely-named temporary file in
    /// the target directory, then atomically moves it onto
    /// <paramref name="targetPath"/> (overwriting). The unique temp name ensures
    /// two concurrent writers of identical content never collide on the staging
    /// file, and the final rename guarantees readers never observe a partial blob.
    /// Orphan temp files (left only on crash/cancel) are best-effort cleaned up.
    /// </summary>
    private static async Task WriteAtomicAsync(
        string targetPath,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        var tmp = targetPath + "." + Path.GetRandomFileName() + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                tmp,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            MoveIntoPlace(tmp, targetPath);
        }
        catch
        {
            TryDeleteTemp(tmp);
            throw;
        }
    }

    /// <summary>
    /// Atomic-write variant for the small sidecar text file (content type hint).
    /// </summary>
    private static async Task WriteAtomicTextAsync(
        string targetPath,
        string content,
        CancellationToken cancellationToken)
    {
        var tmp = targetPath + "." + Path.GetRandomFileName() + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tmp, content, cancellationToken).ConfigureAwait(false);
            MoveIntoPlace(tmp, targetPath);
        }
        catch
        {
            TryDeleteTemp(tmp);
            throw;
        }
    }

    /// <summary>
    /// Renames the staged temp file onto its final content-addressed path, tolerating concurrent
    /// writers. Because the path is content-addressed, two writers producing the same content target
    /// the same path; on Windows their <see cref="File.Move(string, string, bool)"/> calls can race
    /// and throw <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> while the
    /// destination is briefly held. When the destination already exists the (identical) content is
    /// already durably stored, so the race is benign — discard our temp and succeed. Otherwise retry
    /// briefly to ride out the transient lock window.
    /// </summary>
    private static void MoveIntoPlace(string tmp, string targetPath)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(tmp, targetPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (File.Exists(targetPath))
                {
                    // A concurrent writer already produced this exact content-addressed file.
                    TryDeleteTemp(tmp);
                    return;
                }

                if (attempt >= 4)
                    throw;

                Thread.Sleep(15 * (attempt + 1));
            }
        }
    }

    private static void TryDeleteTemp(string tmp)
    {
        try
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
        catch
        {
            // Best-effort cleanup; an orphan temp file is harmless and never
            // served (it is not at the content-addressed path).
        }
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
