using Domain.AI.Egress;
using Domain.AI.Identity;

namespace Application.AI.Common.Interfaces.Egress;

/// <summary>
/// Append-only audit sink for egress policy decisions. Mirrors the JSONL
/// shape established by <c>IChangeAuditWriter</c> — one line per
/// <see cref="EgressDecision"/>, written before the request is allowed to
/// proceed or rejected so the audit is durable even if the host crashes
/// mid-request.
/// </summary>
/// <remarks>
/// <para>
/// The audit captures every decision regardless of verdict — allows and
/// denies alike. Operators rely on the audit to answer "where is this skill
/// reaching out?" and "what was blocked?" with equal fidelity; an audit that
/// only records denies hides the silent expansion of a skill's outbound
/// surface area over time.
/// </para>
/// </remarks>
public interface IEgressAuditWriter
{
    /// <summary>Append an egress decision to the audit.</summary>
    /// <param name="decision">The decision to record.</param>
    /// <param name="identity">The submitting agent identity (denormalized into the audit line for replayability).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AppendAsync(
        EgressDecision decision,
        AgentIdentity identity,
        CancellationToken cancellationToken);
}
