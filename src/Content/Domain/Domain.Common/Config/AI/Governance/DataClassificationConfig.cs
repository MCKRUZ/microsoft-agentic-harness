namespace Domain.Common.Config.AI.Governance;

/// <summary>
/// Configuration for Purview-backed data classification (classification-aware DLP) on the agent's live
/// tool-call path. Bound from <c>AppConfig:AI:Governance:DataClassification</c> in appsettings.json.
/// </summary>
/// <remarks>
/// <para>
/// Before a tool reads or writes an external asset, the classification gate resolves the asset's
/// Purview sensitivity label and applies the policy expressed here, mapping a label to an
/// <see cref="ClassificationAction"/>. Unlike the content-pattern sanitizers, this is access control
/// driven by the organization's classification metadata, not by scanning output text.
/// </para>
/// <para>
/// <strong>Opt-in.</strong> <see cref="Mode"/> defaults to <see cref="ClassificationEnforcementMode.Off"/>,
/// so a freshly cloned template requires no Purview tenant and incurs no per-invocation cost. The
/// recommended first step for a real deployment is <see cref="ClassificationEnforcementMode.Audit"/>,
/// which records what would be blocked without breaking the agent.
/// </para>
/// </remarks>
public sealed class DataClassificationConfig
{
    /// <summary>
    /// How strongly classification decisions are enforced. <see cref="ClassificationEnforcementMode.Off"/>
    /// (the default) disables the gate entirely.
    /// </summary>
    public ClassificationEnforcementMode Mode { get; init; } = ClassificationEnforcementMode.Off;

    /// <summary>
    /// Maps a Purview sensitivity-label name (case-insensitive) to the action taken when an asset
    /// carries that label. Labels absent from this map fall to <see cref="DefaultAction"/>. Empty by
    /// default, meaning every resolved label uses <see cref="DefaultAction"/> until the operator
    /// authors rules.
    /// </summary>
    public Dictionary<string, ClassificationAction> LabelActions { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The action applied when an asset carries a resolved label that has no explicit entry in
    /// <see cref="LabelActions"/>. Defaults to <see cref="ClassificationAction.Allow"/> so an
    /// unmapped-but-known label does not block the agent until the operator opts in to stricter rules.
    /// </summary>
    public ClassificationAction DefaultAction { get; init; } = ClassificationAction.Allow;

    /// <summary>
    /// The action applied when no classification could be resolved — an unknown asset kind (local file
    /// with no embedded label, arbitrary MCP output) or one Purview has not scanned. Defaults to
    /// <see cref="ClassificationAction.Allow"/> (observe, do not break), matching the template's
    /// opt-in posture. High-security deployments set this to <see cref="ClassificationAction.Block"/>
    /// for fail-closed handling of anything Purview cannot vouch for.
    /// </summary>
    public ClassificationAction UnknownAssetAction { get; init; } = ClassificationAction.Allow;

    /// <summary>
    /// Configuration for the Microsoft Purview Information Protection (MIP) provider — the Microsoft
    /// Graph-backed source of sensitivity labels for documents and files. Opt-in via
    /// <see cref="InformationProtectionProviderConfig.Enabled"/>; off by default.
    /// </summary>
    public InformationProtectionProviderConfig InformationProtection { get; init; } = new();

    /// <summary>
    /// How long a resolved <c>AssetLabelResult</c> is cached, keyed by the asset, before the provider is
    /// consulted again. Caching is mandatory in practice — without it every gated tool call incurs a
    /// Purview round trip. Defaults to five minutes; a non-positive value disables result caching.
    /// Distinct from <see cref="InformationProtectionProviderConfig.LabelCatalogCacheTtl"/>, which caches
    /// the label taxonomy rather than per-asset results.
    /// </summary>
    public TimeSpan ResultCacheTtl { get; init; } = TimeSpan.FromMinutes(5);
}
