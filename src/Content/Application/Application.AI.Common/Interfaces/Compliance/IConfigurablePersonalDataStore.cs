namespace Application.AI.Common.Interfaces.Compliance;

/// <summary>
/// A personal-data store that a host can register without configuring, and that then holds nothing (#1003).
/// </summary>
/// <remarks>
/// <para>
/// Implement it beside <see cref="IPersonalDataEraser"/> and <see cref="IPersonalDataExporter"/> on a store whose backend
/// can be absent while the store is still registered, such as an Azure AI Search index with no endpoint. While
/// <see cref="IsConfigured"/> is <see langword="false"/>, the erasure coordinator lists the store as
/// <c>NotConfigured</c> and the export lists it as not applicable, neither of them calling it. A store that cannot be
/// reached is not this: a configured store that fails must still fail, so its line can say so.
/// </para>
/// <para>
/// A separate interface, not a member of the two store contracts, so a stand-in for those contracts in a test is never
/// read as unconfigured by default.
/// </para>
/// </remarks>
public interface IConfigurablePersonalDataStore
{
    /// <summary>Whether this host configured the store's backend, so it may hold anyone's data.</summary>
    bool IsConfigured { get; }
}
