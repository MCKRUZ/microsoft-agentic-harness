using System.Reflection;
using Application.AI.Common.Interfaces.Compliance;
using Domain.Common.Compliance;

namespace Application.AI.Common.Services.Compliance;

/// <summary>
/// One store the host runs that keeps personal data it cannot search for one person (#900), so the erasure receipt can
/// say it exists, what it keeps for, and whether that leaves the erasure <c>Partial</c>.
/// </summary>
/// <remarks>
/// <para>
/// Registered with <c>AddRetainedPersonalData</c> beside the store's own registration. It wraps the instance the host
/// actually resolved, and reads that instance's <c>[PersonalDataStore]</c> descriptor, so the receipt follows the inventory
/// and the configuration: a store switched off resolves to a null object with no descriptor and is not listed.
/// </para>
/// <para>
/// A store that can erase, or one keyed to the owner, is not listed here: the first appears as an erasure line, the second
/// as <see cref="StoreErasureStatus.NotSupported"/> through <see cref="NonErasableStore"/>.
/// </para>
/// </remarks>
public sealed class RetainedPersonalData
{
    /// <summary>Reason for <see cref="StoreErasureStatus.RetainedIndefinitely"/>.</summary>
    public const string IndefiniteReason =
        "cannot be searched for one person, and nothing ages its records out yet";

    /// <summary>
    /// Reason for <see cref="StoreErasureStatus.RetainedIndefinitely"/> on a store whose retention is configured. Configured
    /// retention can be switched off, and such stores keep open items regardless, so it is not counted as ageing out.
    /// </summary>
    public const string ConfiguredReason =
        "cannot be searched for one person; open records are kept, and closed ones only until a retention period that can be switched off";

    /// <summary>Reason for <see cref="StoreErasureStatus.RetainedUntilExpiry"/> for a store held only in memory.</summary>
    public const string VolatileReason =
        "cannot be searched for one person; held in process memory only and gone when the process restarts";

    /// <summary>
    /// Start of the reason for <see cref="StoreErasureStatus.RetainedByObligation"/>; the kinds of data the trail holds are
    /// appended, so the person can see what an audit trail kept on purpose may still say about them.
    /// </summary>
    public const string ObligationReason =
        "a tamper-evident accountability record kept on purpose; removing one person would break the evidence it provides";

    /// <summary>Initializes a new instance of the <see cref="RetainedPersonalData"/> class.</summary>
    /// <param name="storeName">The store, as a reviewer would name it.</param>
    /// <param name="store">The instance the host resolved, or <see langword="null"/> when it runs none.</param>
    public RetainedPersonalData(string storeName, object? store)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeName);
        StoreName = storeName;
        Store = store;
    }

    /// <summary>Gets the store as the person reading the receipt would name it.</summary>
    public string StoreName { get; }

    /// <summary>Gets the instance the host resolved, or <see langword="null"/> when it runs none.</summary>
    public object? Store { get; }

    /// <summary>
    /// The receipt line for this store, or <see langword="null"/> when the host runs none, it keeps nothing, or it is
    /// reached by erasure in another way.
    /// </summary>
    /// <returns>The line, or <see langword="null"/>.</returns>
    public StoreErasureOutcome? ToOutcome()
    {
        if (Store is null or IPersonalDataEraser)
            return null;

        var descriptor = Store.GetType().GetCustomAttribute<PersonalDataStoreAttribute>();
        if (descriptor is null || descriptor.Erasure == ErasureSupport.PurgeByOwner || descriptor.KeyedBy == PersonalDataKey.Owner)
            return null;

        var (status, reason) = descriptor switch
        {
            { Erasure: ErasureSupport.Exempt } =>
                (StoreErasureStatus.RetainedByObligation, $"{ObligationReason}; it holds {Describe(descriptor.Holds)}"),
            { Retention: RetentionBasis.Volatile } => (StoreErasureStatus.RetainedUntilExpiry, VolatileReason),
            { Retention: RetentionBasis.Configured } => (StoreErasureStatus.RetainedIndefinitely, ConfiguredReason),
            _ => (StoreErasureStatus.RetainedIndefinitely, IndefiniteReason),
        };
        return new StoreErasureOutcome(StoreName, status, 0, reason);
    }

    /// <summary>The categories as plain words, for example "identifiers, tool inputs and outputs".</summary>
    private static string Describe(PersonalDataCategories categories) =>
        string.Join(", ", Enum.GetValues<PersonalDataCategories>()
            .Where(c => c != 0 && categories.HasFlag(c))
            .Select(c => System.Text.RegularExpressions.Regex.Replace(c.ToString(), "(?<!^)([A-Z])", " $1").ToLowerInvariant()));
}
