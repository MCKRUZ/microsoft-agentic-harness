namespace Infrastructure.AI.Audit;

/// <summary>What one replay pass did with the compliance records held pending (#995).</summary>
/// <param name="Written">Records appended to the trail in this pass.</param>
/// <param name="AlreadyInTrail">
/// Records found already in the trail and only removed from the pending folder: a previous pass appended them and stopped
/// before removing them.
/// </param>
/// <param name="SetAside">Held files that could not be read back as a record, renamed so they no longer block the queue.</param>
/// <param name="Remaining">Records still held when the pass ended.</param>
/// <param name="SkippedBecause">
/// Why the pass did nothing, or <see langword="null"/> when it ran: the replay lock could not be taken, usually because
/// another pass holds it, here or in another process, but also when the pending folder cannot be written.
/// </param>
public sealed record PendingRecordsReplay(int Written, int AlreadyInTrail, int SetAside, int Remaining, string? SkippedBecause)
{
    /// <summary>A pass that found nothing held.</summary>
    public static PendingRecordsReplay Nothing { get; } = new(0, 0, 0, 0, SkippedBecause: null);

    /// <summary>Gets whether the pass did nothing because it could not take the replay lock.</summary>
    public bool Skipped => SkippedBecause is not null;
}
