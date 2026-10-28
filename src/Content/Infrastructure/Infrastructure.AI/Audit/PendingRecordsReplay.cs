namespace Infrastructure.AI.Audit;

/// <summary>What one replay pass did with the compliance records held pending (#995).</summary>
/// <param name="Written">Records appended to the trail in this pass.</param>
/// <param name="AlreadyInTrail">
/// Records found already in the trail and only removed from the pending folder: a previous pass appended them and stopped
/// before removing them.
/// </param>
/// <param name="SetAside">Held files that could not be read back as a record, renamed so they no longer block the queue.</param>
/// <param name="Remaining">Records still held when the pass ended.</param>
/// <param name="Skipped">Whether the pass did nothing because another pass, here or in another process, held the folder.</param>
public sealed record PendingRecordsReplay(int Written, int AlreadyInTrail, int SetAside, int Remaining, bool Skipped)
{
    /// <summary>A pass that found nothing held.</summary>
    public static PendingRecordsReplay Nothing { get; } = new(0, 0, 0, 0, Skipped: false);
}
