namespace Domain.Common.Compliance;

/// <summary>What a store can do when a person asks for their data to be erased.</summary>
public enum ErasureSupport
{
    /// <summary>Removes everything it holds for one owner in one operation.</summary>
    PurgeByOwner,

    /// <summary>Removes one record at a time when asked, but cannot find all of one owner's records itself.</summary>
    ItemDeleteOnly,

    /// <summary>Has no way to remove a person's data. A gap, recorded so it is visible.</summary>
    NotSupported,

    /// <summary>
    /// Erasure is deliberately refused, for example a tamper-evident audit trail kept under a legal obligation.
    /// Needs the reason in <see cref="PersonalDataStoreAttribute.Description"/>.
    /// </summary>
    Exempt,
}
