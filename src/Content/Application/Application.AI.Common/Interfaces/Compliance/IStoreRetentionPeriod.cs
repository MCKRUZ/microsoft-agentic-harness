namespace Application.AI.Common.Interfaces.Compliance;

/// <summary>
/// A store that can say, from its live configuration, how long it keeps a record (#901).
/// </summary>
/// <remarks>
/// Read by the erasure receipt for stores that cannot be searched for one person. A store whose descriptor says
/// <c>RetentionBasis.Configured</c> but that does not implement this, or reports no period, is listed as kept
/// indefinitely, because a configured period can be switched off and the receipt must not claim one that is not running.
/// </remarks>
public interface IStoreRetentionPeriod
{
    /// <summary>
    /// The longest a record is kept now, or <see langword="null"/> when nothing deletes records (the sweep is off).
    /// </summary>
    TimeSpan? CurrentRetentionPeriod { get; }
}
