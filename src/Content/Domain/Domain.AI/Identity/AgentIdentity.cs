namespace Domain.AI.Identity;

/// <summary>
/// The workload identity of a running agent. Carried on the agent-execution scope
/// (<c>IAgentExecutionContext</c>) so every outbound tool call, A2A call, and external
/// API call can be attributed to a specific Entra-bound agent principal.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AgentIdentity"/> is the durable RBAC boundary for outbound work. Prompt-level
/// rules degrade under adversarial input; the agent's runtime identity (and the Azure RBAC
/// it carries) does not. Tool-level RBAC reads this identity at every call site and validates
/// against the tool's allowlist.
/// </para>
/// <para>
/// Separate from the human-caller scope (<c>KnowledgeScopeAccessor</c>). The human-caller
/// scope answers "who initiated this work and which tenant's data are we touching?";
/// <see cref="AgentIdentity"/> answers "which agent is currently running on behalf of that
/// human?". Both flow through tool calls; RBAC checks are independent and ANDed.
/// </para>
/// <para>
/// Records throughout the harness validate at boundaries (Application-layer FluentValidation),
/// not in constructors. This record accepts any non-null <see cref="Id"/> and any
/// <see cref="AgentIdentityKind"/>; <see cref="IAgentIdentityValidator"/> enforces the
/// real invariants (no <see cref="AgentIdentityKind.Unspecified"/>, kind-specific required
/// fields, etc.).
/// </para>
/// </remarks>
public sealed record AgentIdentity
{
    /// <summary>
    /// The agent's stable identifier within the harness. Typically the agent's manifest
    /// id, or the Entra application's <c>appId</c> when bound to a registered Entra
    /// application. Not a per-turn id and not a session id.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The credential kind that backs this identity. Determines which
    /// <c>IAgentCredentialProvider</c> resolved it and which token-acquisition flow
    /// produced its bearer tokens.
    /// </summary>
    public required AgentIdentityKind Kind { get; init; }

    /// <summary>
    /// The Entra (Azure AD) tenant the agent is registered in. Null when the identity
    /// is not Entra-bound (e.g. <see cref="AgentIdentityKind.Development"/>) or when
    /// the tenant is implied by the credential provider's configuration.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// The Entra object id of the agent's service principal. Used by Azure RBAC role
    /// assignments. Null when not Entra-bound.
    /// </summary>
    public string? ObjectId { get; init; }

    /// <summary>
    /// The token audience used when acquiring access tokens for this identity. Typically
    /// an Entra application uri (<c>api://...</c>) or a resource id. Null when the
    /// credential provider's audience is global.
    /// </summary>
    public string? Audience { get; init; }
}
