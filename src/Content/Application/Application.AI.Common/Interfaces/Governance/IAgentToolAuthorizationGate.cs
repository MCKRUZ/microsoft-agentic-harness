namespace Application.AI.Common.Interfaces.Governance;

/// <summary>
/// Admission stage that answers one question: is the <em>agent</em> executing this call
/// permitted to invoke this tool at all?
/// </summary>
/// <remarks>
/// <para>
/// <strong>The second axis of the harness's RBAC.</strong> <c>IKnowledgeScopeValidator</c>
/// asks whether the initiating human may touch a tenant's data. This asks whether the
/// workload identity the agent carries is allowed this tool. The two are independent and
/// ANDed; neither substitutes for the other.
/// </para>
/// <para>
/// <strong>Why this is a gate rather than a direct call to
/// <c>IAgentIdentityValidator</c>.</strong> The validator answers a pure policy question
/// and is unconditionally fail-closed: no policy is a denial. That is the right contract
/// for a policy oracle and the wrong one for a pipeline stage, because a harness that has
/// never configured tool authorization would have every call refused. This seam holds the
/// two things the validator deliberately does not: whether the feature is switched on at
/// all, and how the executing identity is obtained. The validator stays a pure oracle.
/// </para>
/// <para>
/// <strong>Off is a real verdict, not an absence.</strong> An implementation reports its
/// own off state by admitting the call. It is registered unconditionally for the same
/// reason <c>IToolClassificationGate</c> is: an unregistered gate and a switched-off gate
/// would be indistinguishable at runtime, and only one of those is safe.
/// </para>
/// </remarks>
public interface IAgentToolAuthorizationGate
{
    /// <summary>
    /// Decides whether the current execution's agent identity may invoke <paramref name="toolKey"/>.
    /// </summary>
    /// <param name="toolKey">
    /// The tool being admitted — the keyed-DI tool key, or a plan capability token such as
    /// <c>llm_call</c>. Matched against the agent's allowlist as-is; the gate does not treat
    /// capability tokens specially, so a plan-running agent must be granted them explicitly
    /// or hold the wildcard.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels identity acquisition. Cancellation propagates rather than becoming a denial —
    /// an abandoned call is not a policy decision.
    /// </param>
    /// <returns>
    /// An allow when the feature is off or the identity is permitted the tool; otherwise a
    /// denial carrying caller-facing text. Absent or unresolvable identity while the feature
    /// is on is a <em>denial</em>: it is the case where the harness cannot tell who is asking,
    /// which is the one case a permissive answer would be indefensible.
    /// </returns>
    /// <remarks>
    /// Returns <see cref="ToolInvocationDecision"/> rather than a verdict type of its own. The
    /// answer is exactly that type's shape — permitted, or refused with caller-facing text — and
    /// <see cref="IToolCallObserverChain"/>, the other access gate in this chain, already reports
    /// in it. A second identical record would only give the pipeline two vocabularies for one idea.
    /// </remarks>
    ValueTask<ToolInvocationDecision> EvaluateAsync(string toolKey, CancellationToken cancellationToken);
}
