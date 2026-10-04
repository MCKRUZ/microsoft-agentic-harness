namespace Application.AI.Common.Services.Governance;

/// <summary>
/// The inherit-or-mint choices a child governance scope makes when it is armed by
/// <see cref="GovernanceArmer"/>. One named preset per surface that arms a child scope.
/// </summary>
/// <remarks>
/// Each property is a real decision the surfaces differ on, not a toggle added for flexibility.
/// The parent's workload identity is not a choice: it travels whenever the parent has one, so the child
/// is always authorized as the real caller rather than the host's default identity.
/// </remarks>
public sealed record GovernanceArmingPolicy
{
    /// <summary>
    /// <see langword="true"/> mints a fresh one-shot conversation id per arming. <see langword="false"/>
    /// inherits the parent's, falling back to the caller-supplied id when the parent has none.
    /// </summary>
    public required bool MintConversationId { get; init; }

    /// <summary>Where the child's call-once scope id comes from.</summary>
    public required CallOnceScopeSource CallOnceScope { get; init; }

    /// <summary>
    /// <see langword="true"/> starts the child at the parent's turn number (1 when there is none).
    /// <see langword="false"/> always starts at turn 1 — the child is a fresh unit of work.
    /// </summary>
    public required bool InheritTurnNumber { get; init; }

    /// <summary>
    /// A direct tool invocation: one standalone call with no request-level session, so it mints its own
    /// conversation id (keeping #325's retry-attribution memory expiring), omits call-once scope (a null
    /// scope fails the call-once gate open, the documented answer for a surface with no session to key a
    /// repeat-call check on) and starts at turn 1.
    /// </summary>
    public static GovernanceArmingPolicy DirectInvocation { get; } = new()
    {
        MintConversationId = true,
        CallOnceScope = CallOnceScopeSource.Omit,
        InheritTurnNumber = false,
    };

    /// <summary>
    /// A sub-plan: the child is the same principal as the parent, so it inherits conversation id,
    /// call-once scope and turn number. The call-once scope passes through as-is, never re-derived: a
    /// call-once tool the parent already claimed must stay claimed, or a nested plan could call it again.
    /// </summary>
    public static GovernanceArmingPolicy SubPlan { get; } = new()
    {
        MintConversationId = false,
        CallOnceScope = CallOnceScopeSource.InheritAsIs,
        InheritTurnNumber = true,
    };

    /// <summary>
    /// A delegation: a fresh unit of work run as the delegate's own agent, inside the parent's session.
    /// Inherits conversation id and call-once scope (falling back to the delegation id) but starts at
    /// turn 1.
    /// </summary>
    public static GovernanceArmingPolicy Delegation { get; } = new()
    {
        MintConversationId = false,
        CallOnceScope = CallOnceScopeSource.InheritOrFallback,
        InheritTurnNumber = false,
    };
}
