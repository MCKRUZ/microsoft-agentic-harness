using Domain.AI.Escalation;

namespace Application.Core.CQRS.Escalation;

/// <summary>
/// A slim, wire-safe projection of a pending <see cref="EscalationRequest"/>. Deliberately
/// excludes <see cref="EscalationRequest.OriginatingDecision"/> — the internal governance
/// decision that raised the escalation carries rule internals that do not belong on the HTTP
/// surface. Everything an approver needs to decide (what the agent tried to do, with which
/// arguments, at what risk, and by when) is carried explicitly.
/// </summary>
public sealed record EscalationSummary
{
    /// <summary>Unique identifier of the escalation.</summary>
    public required Guid EscalationId { get; init; }

    /// <summary>The agent that attempted the action.</summary>
    public required string AgentId { get; init; }

    /// <summary>The tool or operation the agent tried to invoke.</summary>
    public required string ToolName { get; init; }

    /// <summary>Arguments passed to the tool (already sanitized for audit display upstream).</summary>
    public required IReadOnlyDictionary<string, string> Arguments { get; init; }

    /// <summary>Human-readable summary of the attempted action.</summary>
    public required string Description { get; init; }

    /// <summary>Risk level derived from the matched governance rule.</summary>
    public required RiskLevel RiskLevel { get; init; }

    /// <summary>Urgency of the escalation.</summary>
    public required EscalationPriority Priority { get; init; }

    /// <summary>Strategy used to evaluate the collected approver decisions.</summary>
    public required ApprovalStrategyType ApprovalStrategy { get; init; }

    /// <summary>For the Quorum strategy, the N in N-of-M required approvals. Zero otherwise.</summary>
    public required int QuorumThreshold { get; init; }

    /// <summary>
    /// The approver roster. Visible by construction only to roster members (list and get are
    /// roster-filtered), so exposing it lets co-approvers coordinate on AllOf/Quorum items.
    /// </summary>
    public required IReadOnlyList<string> Approvers { get; init; }

    /// <summary>When the escalation was created.</summary>
    public required DateTimeOffset RequestedAt { get; init; }

    /// <summary>Seconds after <see cref="RequestedAt"/> at which the escalation times out.</summary>
    public required int TimeoutSeconds { get; init; }

    /// <summary>Action the service takes if the escalation times out undecided.</summary>
    public required EscalationTimeoutAction TimeoutAction { get; init; }

    /// <summary>Projects a domain <see cref="EscalationRequest"/> to the wire-safe shape.</summary>
    /// <param name="request">The pending escalation request to project. Must not be null.</param>
    public static EscalationSummary FromRequest(EscalationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new EscalationSummary
        {
            EscalationId = request.EscalationId,
            AgentId = request.AgentId,
            ToolName = request.ToolName,
            Arguments = request.Arguments,
            Description = request.Description,
            RiskLevel = request.RiskLevel,
            Priority = request.Priority,
            ApprovalStrategy = request.ApprovalStrategy,
            QuorumThreshold = request.QuorumThreshold,
            Approvers = request.Approvers,
            RequestedAt = request.RequestedAt,
            TimeoutSeconds = request.TimeoutSeconds,
            TimeoutAction = request.TimeoutAction
        };
    }
}
