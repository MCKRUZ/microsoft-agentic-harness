namespace Domain.Common.Compliance;

/// <summary>
/// Declares that a type is a place personal data is kept, and what a privacy team needs to know about it: what it
/// holds, whose it is, how long it stays, and whether it can be erased or exported (#899).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every type that stores data must carry this, <see cref="PersonalDataChannelAttribute"/>, or an entry in
/// the guard's list of writers that hold no personal data.</strong> <c>EveryPersonalDataStoreIsDeclaredTests</c>
/// reads the compiled code, finds every type that writes to a database, a file, a cache or a storage service, and
/// fails on one that is not accounted for. Erasure was once wired store by store, and the conversation transcripts
/// fell outside it because nobody added them to the list. This attribute is the list, and the guard is what keeps
/// it complete.
/// </para>
/// <para>
/// <strong>Declare what is true now, not what should be.</strong> A store that nothing ever deletes says
/// <see cref="RetentionBasis.Unbounded"/>; one with no way to erase a person's data says
/// <see cref="ErasureSupport.NotSupported"/>. The gaps are the point: they are what retention, erasure and export
/// work is planned against, and a descriptor that claims more than the code does is worse than none.
/// </para>
/// <para>
/// Applied to the type that knows what the data means, usually a repository. A type it writes through (a database
/// context, a shared file writer) carries <see cref="PersonalDataChannelAttribute"/> instead.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class PersonalDataStoreAttribute : Attribute
{
    /// <summary>Initializes a new instance of the <see cref="PersonalDataStoreAttribute"/> class.</summary>
    /// <param name="description">
    /// What the store keeps and where, in a sentence a privacy reviewer can read, including the reason behind any
    /// <see cref="PersonalDataKey.Unkeyed"/>, <see cref="RetentionBasis.KeptByDesign"/>,
    /// <see cref="ErasureSupport.Exempt"/> or <see cref="ExportSupport.NotApplicable"/> choice.
    /// </param>
    /// <param name="holds">Every category of personal data it can hold, under any configuration.</param>
    /// <param name="keyedBy">Whose data each record is, as far as the store can tell.</param>
    /// <param name="retention">How long records stay.</param>
    /// <param name="erasure">What it can do when a person asks to be erased.</param>
    /// <param name="export">What it can do when a person asks for a copy.</param>
    public PersonalDataStoreAttribute(
        string description,
        PersonalDataCategories holds,
        PersonalDataKey keyedBy,
        RetentionBasis retention,
        ErasureSupport erasure,
        ExportSupport export)
    {
        Description = description;
        Holds = holds;
        KeyedBy = keyedBy;
        Retention = retention;
        Erasure = erasure;
        Export = export;
    }

    /// <summary>What the store keeps and where, with the reasons behind any exception.</summary>
    public string Description { get; }

    /// <summary>Every category of personal data it can hold.</summary>
    public PersonalDataCategories Holds { get; }

    /// <summary>Whose data each record is.</summary>
    public PersonalDataKey KeyedBy { get; }

    /// <summary>How long records stay.</summary>
    public RetentionBasis Retention { get; }

    /// <summary>What it can do when a person asks to be erased.</summary>
    public ErasureSupport Erasure { get; }

    /// <summary>What it can do when a person asks for a copy.</summary>
    public ExportSupport Export { get; }
}
