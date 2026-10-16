namespace Infrastructure.AI.Persistence.Entities;

/// <summary>
/// The claim one process holds on a skill-training run (#853), so two processes cannot work on the same
/// run id at once.
/// </summary>
/// <remarks>
/// A lease, not a lock: it expires if its holder stops renewing it, so a crashed process does not block the
/// run for good. Times are Unix milliseconds in UTC; the comparison happens in SQL, where the claim must be
/// decided atomically.
/// </remarks>
public sealed class SkillTrainingRunLeaseEntity
{
    /// <summary>The run being worked on; the key.</summary>
    public required string RunId { get; set; }

    /// <summary>A token unique to the holder; only the holder may renew or release.</summary>
    public required string OwnerToken { get; set; }

    /// <summary>When the lease lapses unless renewed, in Unix milliseconds (UTC).</summary>
    public long ExpiresAtUnixMs { get; set; }
}
