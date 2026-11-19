namespace Domain.AI.Audit;

/// <summary>
/// Another process held an audit chain's lock for longer than the writer waits (#416). Not tampering and
/// not a broken chain: the chain is busy, and the operation can be retried. A verification that throws
/// this has verified nothing, so a caller reports the chain as not verified rather than as intact.
/// </summary>
public sealed class AuditChainBusyException : IOException
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="location">The chain's location, for the message.</param>
    /// <param name="waited">How long the writer waited.</param>
    /// <param name="inner">The last failure to take the lock.</param>
    public AuditChainBusyException(string location, TimeSpan waited, Exception inner)
        : base($"The audit chain at {location} is busy: another writer held its lock for more than {waited.TotalSeconds:0.###} seconds.", inner)
    {
    }
}
