using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.AI.Common.Interfaces.Audit;
using Domain.AI.Audit;
using Application.Common.Helpers;
using Domain.Common.Config;
using Domain.Common.Config.AI.Audit;
using Infrastructure.AI.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Audit;

/// <summary>
/// Keeps the last verified <see cref="AuditChainCheckpoint"/> for each audit chain: one small JSON file
/// per chain and location, in a <c>checkpoints</c> folder beside the verification receipts (#416).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Keyed by chain and location.</strong> The file name carries a short hash of the chain's
/// storage location, so an operator who moves a log or resets its storage gets a fresh baseline rather
/// than a permanent "truncated" alarm for a log that was never tampered with. Changing the location
/// needs configuration write access, which is the same trust the location itself already relies on.
/// </para>
/// <para>
/// <strong>Also held in memory.</strong> The furthest checkpoint seen by this process is kept as well,
/// so deleting the file while the host runs cannot make the next pass silently re-baseline onto a
/// regrown chain. After a restart only the file is left, which is the stated limit: whoever can write
/// both the log and this folder (by default the same service account) can remove a checkpoint, and the
/// first pass after that re-baselines with a warning rather than an alarm.
/// </para>
/// <para>
/// A missing, empty or unreadable file reads as "no checkpoint". Read and write failures of the kinds
/// the file system raises are logged and swallowed; a checkpoint strengthens a verification that already
/// ran, and failing to keep one must not stop the verifier.
/// </para>
/// </remarks>
public sealed class AuditChainCheckpointStore : IAuditChainCheckpoints
{
    private readonly LastKnownGoodSetting<AuditConfig> _audit;
    private readonly ILogger<AuditChainCheckpointStore> _logger;
    private readonly ConcurrentDictionary<string, AuditChainCheckpoint> _furthestSeen = new(StringComparer.Ordinal);

    /// <summary>Initializes the store.</summary>
    /// <param name="config">Supplies the receipt path the checkpoints live beside, read per call.</param>
    /// <param name="logger">Logger for read and write failures.</param>
    public AuditChainCheckpointStore(IOptionsMonitor<AppConfig> config, ILogger<AuditChainCheckpointStore> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;

        // Read through the same last-known-good guard the verifier uses (#821): a hot-reloaded value that
        // fails validation must not stop verification, and the shipped receipt path is a safe fallback.
        _audit = new LastKnownGoodSetting<AuditConfig>(
            () => config.CurrentValue.AI.Audit, new AuditConfig(), logger, "AppConfig:AI:Audit");
    }

    /// <inheritdoc />
    public async Task<AuditChainCheckpoint?> ReadAsync(IVerifiableAuditChain chain, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chain);

        var key = KeyFor(chain);
        _furthestSeen.TryGetValue(key, out var inMemory);
        var onDisk = await ReadFileAsync(chain, cancellationToken).ConfigureAwait(false);

        if (inMemory is not null && (onDisk is null || onDisk.Sequence < inMemory.Sequence))
        {
            if (onDisk is null)
            {
                _logger.LogWarning(
                    "The checkpoint file for audit chain '{AuditName}' is missing although this process recorded one; " +
                    "using the one held in memory.",
                    chain.AuditName);
            }

            return inMemory;
        }

        return onDisk;
    }

    /// <inheritdoc />
    public async Task WriteAsync(IVerifiableAuditChain chain, AuditChainCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(checkpoint);

        _furthestSeen.AddOrUpdate(KeyFor(chain), checkpoint,
            (_, existing) => checkpoint.Sequence >= existing.Sequence ? checkpoint : existing);

        if (LocationOf(chain) is not { } path)
            return;

        try
        {
            // Owner-only, like the receipts beside it.
            OwnerOnlyDirectoryHelper.Create(Path.GetDirectoryName(path)!, _logger);

            // Written to a temporary file, flushed to disk, then moved into place, so neither a crash nor
            // a power loss mid-write can leave a torn or empty checkpoint in the place of a good one.
            var temp = path + ".tmp";
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, checkpoint, cancellationToken: cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not record the audit checkpoint for '{AuditName}'.", chain.AuditName);
        }
    }

    /// <inheritdoc />
    public string? LocationOf(IVerifiableAuditChain chain)
    {
        ArgumentNullException.ThrowIfNull(chain);

        var receiptPath = _audit.Current.ReceiptPath;
        return string.IsNullOrWhiteSpace(receiptPath)
            ? null
            : Path.Combine(receiptPath, "checkpoints", KeyFor(chain) + ".json");
    }

    private async Task<AuditChainCheckpoint?> ReadFileAsync(IVerifiableAuditChain chain, CancellationToken cancellationToken)
    {
        if (LocationOf(chain) is not { } path || !File.Exists(path))
            return null;

        try
        {
            await using var stream = File.OpenRead(path);
            var checkpoint = await JsonSerializer.DeserializeAsync<AuditChainCheckpoint>(
                stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return checkpoint is { Sequence: >= 0, Hash.Length: > 0 } ? checkpoint : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex,
                "Could not read the audit checkpoint for '{AuditName}'; verifying it for internal consistency only this pass.",
                chain.AuditName);
            return null;
        }
    }

    /// <summary>
    /// The chain's name made safe for a file name, plus a short hash of its storage location so a moved
    /// or reset log starts its own baseline.
    /// </summary>
    private static string KeyFor(IVerifiableAuditChain chain)
    {
        var safeName = string.Concat(chain.AuditName.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        var location = Path.GetFullPath(chain.AuditLocation);
        var locationHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(location)))[..12];
        return $"{safeName}-{locationHash}";
    }
}
