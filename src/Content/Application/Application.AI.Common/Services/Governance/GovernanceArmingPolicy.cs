namespace Application.AI.Common.Services.Governance;

/// <summary>
/// The inherit-or-mint choices a child governance scope makes when it is armed by
/// <see cref="GovernanceArmer"/>. One named preset per surface that arms a child scope, each recording
/// that surface's deliberate choices in one place instead of re-deriving them by hand.
/// </summary>
/// <remarks>
/// Every property is a real decision a surface makes, not a toggle added for flexibility: the three
/// presets differ on exactly these axes, and a fourth surface (a Magentic participant, #769) should
/// pick or add a preset rather than hand-roll the arming sequence — that duplication is what drifted
/// the earlier copies apart.
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
    /// Whether the parent's resolved workload identity (an A2A- or identity-propagated caller) is
    /// stamped onto the child, so it is authorized as the real caller rather than the host's default.
    /// </summary>
    public required bool PropagateWorkloadIdentity { get; init; }

    /// <summary>
    /// Whether to resolve the child scope's <c>IToolCallAdmissionPipeline</c> and reset it. Off for a
    /// surface whose step executors call the child scope's pipeline directly rather than through the
    /// ambient <see cref="ToolAdmissionAccessor"/>.
    /// </summary>
    public required bool ResolvePipeline { get; init; }

    /// <summary>
    /// A direct tool invocation: one standalone call with no request-level session, so it mints its own
    /// conversation id (keeping #325's retry-attribution memory expiring), omits call-once scope and
    /// starts at turn 1.
    /// </summary>
    public static GovernanceArmingPolicy DirectInvocation { get; } = new()
    {
        MintConversationId = true,
        CallOnceScope = CallOnceScopeSource.Omit,
        InheritTurnNumber = false,
        PropagateWorkloadIdentity = false,
        ResolvePipeline = true,
    };

    /// <summary>
    /// A sub-plan: the child is the same principal as the parent, so it inherits conversation id,
    /// call-once scope (as-is — a call-once tool claimed by the parent stays claimed), turn number and
    /// workload identity. It does not resolve a pipeline: its step executors call the child scope's own.
    /// </summary>
    public static GovernanceArmingPolicy SubPlan { get; } = new()
    {
        MintConversationId = false,
        CallOnceScope = CallOnceScopeSource.InheritAsIs,
        InheritTurnNumber = true,
        PropagateWorkloadIdentity = true,
        ResolvePipeline = false,
    };

    /// <summary>
    /// A delegation: a fresh unit of work run as the delegate's own agent, inside the parent's session.
    /// Inherits conversation id and call-once scope (falling back to the delegation id), and workload
    /// identity, but starts at turn 1.
    /// </summary>
    public static GovernanceArmingPolicy Delegation { get; } = new()
    {
        MintConversationId = false,
        CallOnceScope = CallOnceScopeSource.InheritOrFallback,
        InheritTurnNumber = false,
        PropagateWorkloadIdentity = true,
        ResolvePipeline = true,
    };
}
