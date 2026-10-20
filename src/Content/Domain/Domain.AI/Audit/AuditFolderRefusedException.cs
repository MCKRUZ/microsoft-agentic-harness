namespace Domain.AI.Audit;

/// <summary>
/// An audit chain's folder holds a file that would let records be written where verification does not read them,
/// so the chain is neither written nor verified until someone deals with the file (#416). Not a broken chain:
/// nothing has been verified, and deleting the verification checkpoint does not clear it.
/// </summary>
public sealed class AuditFolderRefusedException : IOException
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="message">What was found and what to do about it.</param>
    public AuditFolderRefusedException(string message)
        : base(message)
    {
    }
}
