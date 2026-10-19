using System.Text.Json;
using Domain.AI.Audit;
using Infrastructure.AI.Helpers;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Audit;

/// <summary>
/// Keeps the last verified <see cref="AuditChainCheckpoint"/> for each audit chain, one small JSON file
/// per chain in a <c>checkpoints</c> folder beside the verification receipts (#416).
/// </summary>
/// <remarks>
/// <para>
/// A missing or unreadable checkpoint reads as "none", which verifies the chain for internal
/// consistency only, exactly as before checkpoints existed. That is the right failure for a first run
/// and for a host that has never verified; it is also what an attacker gets by deleting the checkpoint,
/// which is why the verifier writes the head to its structured log as well, where it leaves the host
/// when log export is on.
/// </para>
/// <para>
/// Never throws: a checkpoint is a strengthening of a verification that already ran, and failing to
/// read or record one must not stop the verifier.
/// </para>
/// </remarks>
internal sealed class AuditChainCheckpointStore
{
    private readonly string _directory;
    private readonly ILogger _logger;

    /// <summary>Initializes a store under <paramref name="receiptPath"/>.</summary>
    /// <param name="receiptPath">The verification receipt directory; checkpoints live in a subfolder of it.</param>
    /// <param name="logger">Logger for read and write failures.</param>
    public AuditChainCheckpointStore(string receiptPath, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiptPath);
        ArgumentNullException.ThrowIfNull(logger);
        _directory = Path.Combine(receiptPath, "checkpoints");
        _logger = logger;
    }

    /// <summary>Reads the last checkpoint for <paramref name="auditName"/>, or <c>null</c> when there is none.</summary>
    /// <param name="auditName">The chain's name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<AuditChainCheckpoint?> ReadAsync(string auditName, CancellationToken cancellationToken)
    {
        var path = PathFor(auditName);
        if (!File.Exists(path))
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
                auditName);
            return null;
        }
    }

    /// <summary>Records <paramref name="checkpoint"/> as the last verified point for <paramref name="auditName"/>.</summary>
    /// <param name="auditName">The chain's name.</param>
    /// <param name="checkpoint">The new checkpoint.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task WriteAsync(string auditName, AuditChainCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        try
        {
            // Owner-only, like the receipts beside it.
            OwnerOnlyDirectoryHelper.Create(_directory, _logger);

            // Written to a temporary file and moved into place, so a crash mid-write leaves the previous
            // checkpoint rather than a torn one.
            var path = PathFor(auditName);
            var temp = path + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(checkpoint), cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not record the audit checkpoint for '{AuditName}'.", auditName);
        }
    }

    private string PathFor(string auditName)
    {
        // Audit names are fixed identifiers ("governance", "egress", ...); refuse anything that could
        // leave the folder rather than trust that.
        var safe = string.Concat(auditName.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        return Path.Combine(_directory, safe + ".json");
    }
}
