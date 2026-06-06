using System.Security.Cryptography;
using Application.AI.Common.Interfaces.Changes;
using Domain.Common.Config;
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
        var hash = HashPrefix + Base64Url(hashBytes);
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

        var path = PathFor(evidenceHash);
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
        // hash format: "sha256:<base64url>". Strip prefix; use first 2 chars as fan-out.
        var stripped = evidenceHash.StartsWith(HashPrefix, StringComparison.Ordinal)
            ? evidenceHash[HashPrefix.Length..]
            : evidenceHash;
        var prefix = stripped.Length >= 2 ? stripped[..2] : "_";
        return Path.Combine(_root, prefix, stripped + ".bin");
    }

    private static string Base64Url(byte[] bytes)
    {
        var s = Convert.ToBase64String(bytes);
        return s.Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
