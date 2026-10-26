using Domain.Common.Config.Azure;

namespace Domain.Common.Config.AI.ContentSafety;

/// <summary>
/// Which service screens request text for harmful content (#904). Bound from <c>AppConfig:AI:ContentSafety</c>.
/// </summary>
/// <remarks>
/// <para>
/// The default, <see cref="ContentSafetyProvider.AllowAll"/>, screens nothing: every request passes, and a warning at
/// startup says so outside the Development environment. It exists so the pipeline runs without an Azure resource, not as
/// a control. <see cref="ContentSafetyProvider.AzureAIContentSafety"/> sends request text to Azure AI Content Safety and
/// blocks it when any configured category reaches <see cref="BlockAtSeverity"/>.
/// </para>
/// <para>
/// Only request input is screened. Model output is not passed through this service.
/// </para>
/// </remarks>
public sealed class ContentSafetyConfig
{
    /// <summary>Gets or sets the screening provider. Defaults to <see cref="ContentSafetyProvider.AllowAll"/>.</summary>
    public ContentSafetyProvider Provider { get; set; } = ContentSafetyProvider.AllowAll;

    /// <summary>
    /// Gets or sets the Azure AI Content Safety endpoint, such as <c>https://{resource}.cognitiveservices.azure.com/</c>.
    /// Required for <see cref="ContentSafetyProvider.AzureAIContentSafety"/>.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// Gets or sets an API key for the resource. Leave empty to authenticate with <see cref="Auth"/> (managed identity,
    /// a service principal or the developer's own sign-in), which is preferred; set from a secret store when used.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>Gets or sets the Entra credential used when <see cref="ApiKey"/> is empty.</summary>
    public EntraCredentialConfig Auth { get; set; } = new();

    /// <summary>The categories the service analyses, and the ones screened when <see cref="Categories"/> is empty.</summary>
    public static readonly IReadOnlyList<string> AllCategories = ["Hate", "SelfHarm", "Sexual", "Violence"];

    /// <summary>
    /// Gets or sets the categories screened: any of <c>Hate</c>, <c>SelfHarm</c>, <c>Sexual</c>, <c>Violence</c>.
    /// Empty, the default, screens all four (<see cref="AllCategories"/>).
    /// </summary>
    /// <remarks>
    /// The default is empty rather than the four names because configuration binding adds to a list that already
    /// holds items: a default of all four would turn a configured <c>["Hate"]</c> into five entries, so a deployment
    /// could never narrow the screen.
    /// </remarks>
    public List<string> Categories { get; set; } = [];

    /// <summary>Gets the categories actually screened: <see cref="Categories"/>, or all four when it is empty.</summary>
    public IReadOnlyList<string> ScreenedCategories =>
        Categories.Count == 0 ? AllCategories : [.. Categories.Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Gets or sets the severity at which a category blocks the request, on the service's four-level scale: 0 (safe), 2
    /// (low), 4 (medium), 6 (high). Must be 2, 4 or 6. Defaults to 4, so low-severity matches pass and medium or high
    /// block.
    /// </summary>
    public int BlockAtSeverity { get; set; } = 4;

    /// <summary>
    /// Gets or sets whether a request is refused when its text cannot be screened (the service is unreachable, throttled
    /// or timed out, the credential is rejected, or the analysis is incomplete). Defaults to <see langword="true"/>: a
    /// screening control that lets everything through while it is down is no control. The refusal is a service failure
    /// the caller can retry, not a content block: the text was never judged. <see langword="false"/> lets unscreened text
    /// through, with an error logged.
    /// </summary>
    public bool BlockWhenUnavailable { get; set; } = true;
}

/// <summary>The services that can screen request text.</summary>
public enum ContentSafetyProvider
{
    /// <summary>Screens nothing; every request passes. For development and for hosts with no screening resource.</summary>
    AllowAll = 0,

    /// <summary>Azure AI Content Safety, text analysis.</summary>
    AzureAIContentSafety = 1,
}
