using Domain.Common.Config.Azure;

namespace Domain.Common.Config.AI.Governance;

/// <summary>
/// Configuration for the Microsoft Purview Information Protection (MIP) classification provider — the
/// Microsoft Graph-backed half of the data-classification gate. Bound from
/// <c>AppConfig:AI:Governance:DataClassification:InformationProtection</c>.
/// </summary>
/// <remarks>
/// <para>
/// The provider resolves an asset's sensitivity by mapping the label identifier embedded in the asset
/// against the organization's <em>label taxonomy</em>, which it fetches and caches from Microsoft Graph
/// (<c>GET /security/dataSecurityAndGovernance/sensitivityLabels</c>). Graph supplies the label
/// dictionary (id → name); it does not crack open a file to read the embedded label — that extraction
/// (file → label id) is the asset resolver's job and requires the native MIP File SDK. An asset whose
/// identifier carries no embedded label id therefore resolves to Unknown, which is the common case for
/// ordinary local files.
/// </para>
/// <para>
/// <strong>Opt-in.</strong> <see cref="Enabled"/> defaults to <c>false</c>, so even with classification
/// enforcement switched on the harness only contacts Graph once an operator deliberately wires the MIP
/// provider with a tenant and credentials.
/// </para>
/// </remarks>
public sealed class InformationProtectionProviderConfig
{
    /// <summary>
    /// Whether the Graph-backed Information Protection provider is active. When <c>false</c> (the
    /// default) the harness does not register the provider and never contacts Microsoft Graph.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Base URL of the Microsoft Graph endpoint. Defaults to the global cloud v1.0 endpoint; override
    /// for sovereign clouds (for example <c>https://graph.microsoft.us/v1.0</c> for US Government).
    /// </summary>
    public string GraphBaseUrl { get; init; } = "https://graph.microsoft.com/v1.0";

    /// <summary>
    /// OAuth scopes requested when minting the Graph access token. Defaults to the application-permission
    /// resource scope (<c>.default</c>), which is correct for a managed identity or app registration
    /// holding the <c>SensitivityLabels.Read.All</c> application role required to list tenant labels.
    /// </summary>
    public IReadOnlyList<string> Scopes { get; init; } = ["https://graph.microsoft.com/.default"];

    /// <summary>
    /// Entra credential used to authenticate to Graph. Leave the explicit fields unset to use the
    /// ambient managed identity / default credential chain (the recommended production posture); supply
    /// client-secret or certificate fields for non-managed-identity hosts. The secret, if any, must come
    /// from User Secrets or Key Vault — never appsettings.json.
    /// </summary>
    public EntraCredentialConfig Auth { get; init; } = new();

    /// <summary>
    /// How long the fetched label taxonomy is cached before it is refreshed from Graph. The taxonomy
    /// changes rarely (an admin edits the org's label set), so a long TTL avoids a Graph round trip on
    /// every gated tool call. Defaults to one hour.
    /// </summary>
    public TimeSpan LabelCatalogCacheTtl { get; init; } = TimeSpan.FromHours(1);
}
