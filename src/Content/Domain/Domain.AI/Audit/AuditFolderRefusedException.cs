namespace Domain.AI.Audit;

/// <summary>
/// An audit chain cannot be written or verified as its storage stands (#416): its folder holds a file that would let
/// records be written where verification does not read them, or its archive is configured but not there. Not a
/// broken chain: nothing has been verified, and deleting the verification checkpoint does not clear it.
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
