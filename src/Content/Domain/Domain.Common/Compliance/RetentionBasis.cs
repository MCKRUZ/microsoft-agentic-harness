namespace Domain.Common.Compliance;

/// <summary>How long a store keeps what it holds, as declared on <see cref="PersonalDataStoreAttribute"/>.</summary>
public enum RetentionBasis
{
    /// <summary>A configured period, enforced by something that deletes expired records.</summary>
    Configured,

    /// <summary>Held only in process memory; gone at restart.</summary>
    Volatile,

    /// <summary>
    /// Kept deliberately beyond any routine period, for example a tamper-evident audit trail. Needs the reason in
    /// <see cref="PersonalDataStoreAttribute.Description"/>.
    /// </summary>
    KeptByDesign,

    /// <summary>Nothing deletes it. A gap, recorded so it is visible rather than assumed away.</summary>
    Unbounded,
}
