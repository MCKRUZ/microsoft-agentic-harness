namespace Domain.Common.Config.Azure;

/// <summary>
/// Microsoft Graph API configuration.
/// </summary>
public class GraphApiConfig
{
    /// <summary>
    /// Gets or sets the Graph API permission scopes.
    /// </summary>
    public string[] Scope { get; set; } = ["user.read"];

    /// <summary>
    /// Gets or sets the Entra ID credential configuration for Graph API access.
    /// </summary>
    public EntraCredentialConfig Entra { get; set; } = new();
}
