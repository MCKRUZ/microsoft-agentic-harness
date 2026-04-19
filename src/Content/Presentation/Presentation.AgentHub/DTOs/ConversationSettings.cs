namespace Presentation.AgentHub.DTOs;

/// <summary>
/// Per-conversation agent settings persisted on the <see cref="ConversationRecord"/>.
/// All fields are optional — null means "use the skill or provider default".
/// </summary>
/// <param name="DeploymentName">
/// Model deployment override. When null the skill's declared deployment or
/// <c>AppConfig.AI.AgentFramework.DefaultDeployment</c> is used. Values are validated
/// against <c>AgentFrameworkConfig.AvailableDeployments</c> exposed via
/// <c>GET /api/config/deployments</c>.
/// </param>
/// <param name="Temperature">
/// Sampling temperature override (typically <c>0.0</c>–<c>2.0</c>). Null preserves provider defaults.
/// </param>
/// <param name="SystemPromptOverride">
/// Additional text appended to the skill's base system prompt for this conversation.
/// </param>
public sealed record ConversationSettings(
    string? DeploymentName,
    float? Temperature,
    string? SystemPromptOverride);
