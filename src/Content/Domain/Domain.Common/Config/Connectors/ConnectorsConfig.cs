namespace Domain.Common.Config.Connectors;

/// <summary>
/// Aggregate configuration for all AI connector integrations.
/// Each subsection configures a specific external system connector
/// that the agent can invoke as a tool at runtime.
/// </summary>
/// <remarks>
/// Connectors are opt-in: only connectors with valid configuration
/// (where <c>IsConfigured</c> returns true) are registered as available tools.
/// Unconfigured connectors are still registered but will return graceful
/// "not configured" errors if invoked.
/// </remarks>
public class ConnectorsConfig
{
    /// <summary>
    /// Azure DevOps integration configuration.
    /// </summary>
    public AzureDevOpsConfig AzureDevOps { get; init; } = new();

    /// <summary>
    /// GitHub integration configuration.
    /// </summary>
    public GitHubConfig GitHub { get; init; } = new();

    /// <summary>
    /// Jira integration configuration.
    /// </summary>
    public JiraConfig Jira { get; init; } = new();

    /// <summary>
    /// Slack integration configuration.
    /// </summary>
    public SlackConfig Slack { get; init; } = new();

    /// <summary>
    /// Gets the names of all configured (enabled) connector systems.
    /// Useful for diagnostics and startup logging.
    /// </summary>
    public IReadOnlyList<string> ConfiguredSystems
    {
        get
        {
            var systems = new List<string>();
            if (AzureDevOps.IsConfigured) systems.Add("AzureDevOps");
            if (GitHub.IsConfigured) systems.Add("GitHub");
            if (Jira.IsConfigured) systems.Add("Jira");
            if (Slack.IsConfigured) systems.Add("Slack");
            return systems;
        }
    }
}
