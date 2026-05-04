namespace Domain.AI.Governance;

/// <summary>
/// Classification of detected prompt injection attack type.
/// </summary>
public enum InjectionType
{
    /// <summary>No injection detected.</summary>
    None,
    /// <summary>Direct instruction override ("ignore previous instructions").</summary>
    DirectOverride,
    /// <summary>Delimiter-based escape attempt.</summary>
    DelimiterAttack,
    /// <summary>Base64/hex/unicode encoding to bypass filters.</summary>
    EncodingAttack,
    /// <summary>Role-play or persona manipulation.</summary>
    RolePlay,
    /// <summary>Context window manipulation.</summary>
    ContextManipulation,
    /// <summary>Canary token extraction attempt.</summary>
    CanaryLeak,
    /// <summary>Multi-turn escalation across messages.</summary>
    MultiTurnEscalation
}
