namespace Domain.Common.Compliance;

/// <summary>
/// Declares that a type writes data on behalf of the types that call it, without knowing what the data means:
/// a database context, a shared file writer, a decorator over a store (#899).
/// </summary>
/// <remarks>
/// A channel is not where personal data is described; its callers are. <c>EveryPersonalDataStoreIsDeclaredTests</c>
/// therefore requires every production type that calls a channel to be accounted for in turn — as a store, another
/// channel, or a listed writer that holds no personal data. Without that rule, the audit stores that all write
/// through one shared file writer would be invisible: only the writer touches the file system.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class PersonalDataChannelAttribute : Attribute
{
    /// <summary>Initializes a new instance of the <see cref="PersonalDataChannelAttribute"/> class.</summary>
    /// <param name="description">What it writes through, and for whom.</param>
    public PersonalDataChannelAttribute(string description) => Description = description;

    /// <summary>What it writes through, and for whom.</summary>
    public string Description { get; }
}
