namespace Domain.Common.Config.Connectors;

/// <summary>
/// Configuration for Slack integration.
/// Supports two authentication modes: Bot Token (full API) or Webhook URL (simple notifications).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Bot Token mode</strong> — enables full API access (messages, file uploads, threaded replies).
/// Required scopes: chat:write, channels:read, groups:read, files:write.
/// </para>
/// <para>
/// <strong>Webhook mode</strong> — simpler setup for one-way notifications only.
/// Does not support file uploads or threaded replies.
/// </para>
/// <para>
/// If both are configured, Bot Token takes precedence for operations that support it.
/// </para>
/// </remarks>
public class SlackConfig
{
    /// <summary>
    /// Bot User OAuth Token for API calls. Starts with "xoxb-".
    /// Store in User Secrets (dev) or Azure Key Vault (prod) — never in appsettings.json.
    /// </summary>
    public string? BotToken { get; init; }

    /// <summary>
    /// Default channel for notifications.
    /// Examples: "#general", "#devops-notifications"
    /// </summary>
    public string? DefaultChannel { get; init; }

    /// <summary>
    /// Incoming webhook URL for simple notifications (alternative to BotToken).
    /// </summary>
    public string? WebhookUrl { get; init; }

    /// <summary>
    /// Request timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Whether this configuration is valid and complete for use.
    /// Either BotToken or WebhookUrl must be configured.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BotToken) ||
        !string.IsNullOrWhiteSpace(WebhookUrl);
}
