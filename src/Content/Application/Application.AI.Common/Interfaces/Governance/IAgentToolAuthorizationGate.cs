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
    ValueTask<AgentToolAuthorizationVerdict> EvaluateAsync(string toolKey, CancellationToken cancellationToken);
}

/// <summary>
/// The authorization stage's verdict for one tool call.
/// </summary>
/// <remarks>
/// Constructible only through the factories, matching <c>ToolCallAdmission</c>: a refusal
/// that carried no text would reach a model as an empty successful result, which reads as
/// the tool having run and returned nothing rather than as a refusal.
/// </remarks>
public sealed record AgentToolAuthorizationVerdict
{
    private static readonly AgentToolAuthorizationVerdict AllowedVerdict = new(true, null);

    private AgentToolAuthorizationVerdict(bool isAllowed, string? deniedMessage)
    {
        IsAllowed = isAllowed;
        DeniedMessage = deniedMessage;
    }

    /// <summary>Whether the agent may invoke the tool.</summary>
    public bool IsAllowed { get; }

    /// <summary>
    /// Caller-facing refusal text: never null or blank on a denial, always null on an allow.
    /// Deliberately uninformative about <em>why</em> — see <c>GovernanceDenials</c>.
    /// </summary>
    public string? DeniedMessage { get; }

    /// <summary>The agent may invoke the tool, or the feature is switched off.</summary>
    public static AgentToolAuthorizationVerdict Allow() => AllowedVerdict;

    /// <summary>The agent may not invoke the tool.</summary>
    /// <param name="deniedMessage">
    /// The refusal text. Blank is rejected as well as null, because whitespace reaches a
    /// model as indistinguishable from an empty result.
    /// </param>
    public static AgentToolAuthorizationVerdict Deny(string deniedMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deniedMessage);
        return new AgentToolAuthorizationVerdict(false, deniedMessage);
    }
}
