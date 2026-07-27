using Domain.AI.Bundles;
using Domain.AI.Planner;
using Domain.Common;

namespace Application.AI.Common.Interfaces.Planner;

/// <summary>
/// Drives a single plan execution under a caller's <see cref="CapabilityEnvelope"/> and governance
/// identity. This is the one place the security-critical ambient arming for a plan run lives, so
/// every trigger of an enveloped plan run (the workflow HTTP surface, background dispatchers, any
/// future host entry point) shares it — mirroring the <c>IBundleRunExecutor</c> doctrine for bundle
/// runs.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What it does.</strong> Given a <see cref="PlanRunRequest"/>, it initializes the scoped
/// agent execution context with the caller's governance identity, publishes the caller's capability
/// envelope ambiently, and drives <see cref="IPlanExecutor.ExecuteAsync(PlanId, CancellationToken)"/>
/// to completion inside that scope. The envelope is torn down on every path — success, failure, or
/// exception — before this method returns.
/// </para>
/// <para>
/// <strong>Why the arming lives here.</strong> The tool-invocation governor enforces the per-caller
/// grant only when an envelope is published ambiently for the running flow, and it fails
/// <em>closed</em> on any identity-less tool call made under an envelope — so both halves (identity
/// and envelope) must be armed together, in one place. Scattering that arming across callers is how
/// one trigger silently diverges and opens the gate; concentrating it here is what makes the plan
/// engine's <c>ToolUse</c>, <c>Retrieval</c>, and autonomy-ceiling checks impossible to skip on this
/// path. The envelope scope stays open across the entire execution: the plan summary is fully
/// materialised before this method returns, so there is no deferred enumeration that could outlive
/// the scope.
/// </para>
/// <para>
/// <strong>Fail-closed posture.</strong> This path never runs un-enveloped: a request with no
/// envelope or no agent identity is rejected before any plan state is touched. By contrast, direct
/// in-process <see cref="IPlanExecutor"/> callers (tests, trusted host code) arm nothing and remain
/// ungoverned-by-default exactly as before this executor existed — the plan engine's envelope checks
/// are all conditioned on an ambient envelope being present.
/// </para>
/// <para>
/// <strong>Ownership is not checked here.</strong> The executor authorizes nothing about the
/// caller-to-plan relationship — it drives whatever plan id it is given under whatever envelope it
/// is handed. Callers are responsible for having resolved the envelope from the caller's credential
/// and for having verified the requesting principal may execute the plan (the planner scope filter
/// governs plan visibility). Do not expose this to an unauthenticated surface.
/// </para>
/// </remarks>
public interface IPlanRunExecutor
{
    /// <summary>
    /// Executes the plan identified by <see cref="PlanRunRequest.PlanId"/> under the request's
    /// capability envelope and governance identity.
    /// </summary>
    /// <param name="request">The plan to run and the grant to confine it to.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>
    /// The plan execution summary, or a failure carrying a stable error code
    /// (<c>plan_run.envelope_required</c>, <c>plan_run.agent_identity_required</c>,
    /// <c>plan_run.agent_identity_invalid</c>, <c>plan_run.execution_failed</c>) — never raw
    /// exception text.
    /// </returns>
    Task<Result<PlanExecutionSummary>> ExecuteAsync(PlanRunRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// A request to execute one plan under one caller's capability envelope. The envelope and agent
/// identity are required by design: this record cannot express an ungoverned run.
/// </summary>
public sealed record PlanRunRequest
{
    /// <summary>The plan to execute.</summary>
    public required PlanId PlanId { get; init; }

    /// <summary>
    /// The capability grant confining the run — the tools its steps may invoke, the retrieval
    /// capability, and the autonomy ceiling. Resolved by the caller (from the caller's credential),
    /// never from the plan itself.
    /// </summary>
    public required CapabilityEnvelope Envelope { get; init; }

    /// <summary>
    /// The governance identity the run executes as. Tool permissions resolve against this id and
    /// every governance decision is audited under it. The governor fails closed on enveloped tool
    /// calls without an identity, so this is required.
    /// </summary>
    /// <remarks>
    /// Constrained to <see cref="MaxAgentIdLength"/> characters of
    /// <c>[A-Za-z0-9._:-]</c> — see <see cref="IsWellFormedAgentId"/>. This value is the
    /// permission-resolution key and the audit subject: it is glob-matched against permission rules
    /// and written into audit records, so an unbounded or punctuation-bearing id is both a
    /// rule-matching hazard (wildcards, separators) and a log-forging one (newlines).
    /// </remarks>
    public required string AgentId { get; init; }

    /// <summary>Maximum accepted length of <see cref="AgentId"/>.</summary>
    public const int MaxAgentIdLength = 128;

    /// <summary>
    /// Whether <paramref name="agentId"/> is safe to use as a permission-resolution key and audit
    /// subject: non-blank, at most <see cref="MaxAgentIdLength"/> characters, and restricted to
    /// ASCII letters, digits, and the separators <c>. _ : -</c>.
    /// </summary>
    /// <param name="agentId">The candidate identity.</param>
    /// <returns><c>true</c> when the value is well formed.</returns>
    public static bool IsWellFormedAgentId(string? agentId) =>
        !string.IsNullOrWhiteSpace(agentId)
        && agentId.Length <= MaxAgentIdLength
        && agentId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-');

    /// <summary>
    /// Optional conversation/session identifier for the execution context. Defaults to the plan id
    /// when omitted.
    /// </summary>
    public string? ConversationId { get; init; }
}
