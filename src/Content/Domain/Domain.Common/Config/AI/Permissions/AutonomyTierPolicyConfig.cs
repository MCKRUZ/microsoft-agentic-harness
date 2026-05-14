namespace Domain.Common.Config.AI.Permissions;

/// <summary>
/// Configuration for a single autonomy tier's permission policy.
/// Bound from <c>AppConfig:AI:Permissions:TierPolicies:{TierName}</c>.
/// </summary>
public class AutonomyTierPolicyConfig
{
    /// <summary>
    /// Default permission behavior for this tier. Valid values: "Allow", "Ask", "Deny".
    /// Restricted and Supervised default to "Ask"; Autonomous defaults to "Allow".
    /// </summary>
    public string DefaultBehavior { get; set; } = "Ask";

    /// <summary>
    /// Per-tool behavior overrides within this tier. Key is tool name, value is behavior
    /// ("Allow", "Ask", "Deny"). Enables specific tools for otherwise restricted agents.
    /// </summary>
    public Dictionary<string, string>? ToolOverrides { get; set; }

    /// <summary>
    /// Controls agent behavior while an escalation is pending for this tier.
    /// Valid values: "Block" (agent waits for outcome) or "QueueAndContinue" (agent proceeds).
    /// Defaults to "Block" for safety.
    /// </summary>
    public string EscalationBehavior { get; set; } = "Block";
}
