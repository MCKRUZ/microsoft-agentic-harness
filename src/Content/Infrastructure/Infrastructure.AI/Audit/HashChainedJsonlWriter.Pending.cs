namespace Infrastructure.AI.Audit;

/// <summary>
/// The hold-and-replay half of <see cref="HashChainedJsonlWriter"/> (#1001): a writer built from an
/// <see cref="AuditChainLocation"/> holds every record it cannot append, and replays them into its own chain.
/// </summary>
/// <remarks>
/// Only the location constructor gives a writer a pending folder, and every one of the six audit trails is built through it,
/// so all six keep a record their chain refused. A writer built from bare paths (tests, tooling) has none: its failed append
/// is a failure and nothing more.
/// </remarks>
public sealed partial class HashChainedJsonlWriter
{
    private readonly PendingAuditRecords? _pending;

    /// <summary>Gets the folder this writer holds records it could not append in, or <see langword="null"/> when it has none.</summary>
    public string? PendingLocation => _pending?.Folder;

    /// <summary>Gets how many records are held for replay now; zero for a writer with no pending folder.</summary>
    public int HeldRecordCount => _pending?.CountHeld() ?? 0;

    /// <summary>Gets the pending store, for tests that replace its seams.</summary>
    internal PendingAuditRecords? Pending => _pending;

    /// <summary>
    /// Writes every record held in this chain's pending folder into the chain, oldest first (see
    /// <see cref="PendingAuditRecords.ReplayAsync"/>).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the pass did; <see cref="PendingRecordsReplay.Nothing"/> for a writer with no pending folder.</returns>
    public Task<PendingRecordsReplay> ReplayPendingAsync(CancellationToken cancellationToken) =>
        _pending is null
            ? Task.FromResult(PendingRecordsReplay.Nothing)
            : _pending.ReplayAsync(this, cancellationToken);
}
