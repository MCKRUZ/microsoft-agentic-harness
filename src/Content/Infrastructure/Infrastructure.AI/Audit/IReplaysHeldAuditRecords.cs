namespace Infrastructure.AI.Audit;

/// <summary>
/// An audit trail that holds a record it could not append and writes it in later (#995, #1001): each of the six shipped
/// trails. <see cref="AuditRecordReplayService"/> finds them among the registered
/// <see cref="Application.AI.Common.Interfaces.Audit.IVerifiableAuditChain"/>s and replays each one.
/// </summary>
/// <remarks>
/// A deployment that replaces a trail with one of its own that does not implement this is simply not replayed: a
/// replacement owns its own failures.
/// </remarks>
public interface IReplaysHeldAuditRecords
{
    /// <summary>Gets the trail's name, as <c>IVerifiableAuditChain.AuditName</c> gives it.</summary>
    string AuditName { get; }

    /// <summary>Gets the folder the trail holds records it could not append in.</summary>
    string PendingLocation { get; }

    /// <summary>Writes every held record into the trail, oldest first, and removes each once it is there.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the pass did.</returns>
    Task<PendingRecordsReplay> ReplayPendingAsync(CancellationToken cancellationToken);
}
