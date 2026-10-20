namespace Domain.Common.Compliance;

/// <summary>What a store can do when a person asks for a copy of their data.</summary>
public enum ExportSupport
{
    /// <summary>Can return everything it holds for one owner.</summary>
    ByOwner,

    /// <summary>Cannot return one owner's data. A gap, recorded so it is visible.</summary>
    NotSupported,

    /// <summary>
    /// Holds nothing a person would receive a copy of, for example operational diagnostics. Needs the reason in
    /// <see cref="PersonalDataStoreAttribute.Description"/>.
    /// </summary>
    NotApplicable,
}
