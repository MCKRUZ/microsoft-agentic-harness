namespace Domain.Common.Compliance;

/// <summary>Whose data a store's records are, as far as the store itself can tell.</summary>
public enum PersonalDataKey
{
    /// <summary>Each record names the person it belongs to, so it can be found by that person.</summary>
    Owner,

    /// <summary>Records name a tenant but not a person.</summary>
    Tenant,

    /// <summary>
    /// Records carry no owner. Finding one person's data means searching content. Needs a reason in
    /// <see cref="PersonalDataStoreAttribute.Description"/>.
    /// </summary>
    Unkeyed,
}
