using Domain.AI.Escalation;

namespace Application.Core.Tests.CQRS.Escalation;

/// <summary>
/// Shared fixture factories for the escalation CQRS test suite: a well-formed pending request
/// and a resolved outcome, with overridable identity fields.
/// </summary>
internal static class EscalationTestData
{
    /// <summary>Creates a pending escalation request with the given roster.</summary>
    public static EscalationRequest NewRequest(Guid? id = null, params string[] approvers) => new()
    {
        EscalationId = id ?? Guid.NewGuid(),
        AgentId = "agent-1",
        ToolName = "file_system",
        Arguments = new Dictionary<string, string> { ["path"] = "/etc/hosts" },
        Description = "Agent wants to write outside the workspace",
        RiskLevel = RiskLevel.High,
        Priority = EscalationPriority.Blocking,
        ApprovalStrategy = ApprovalStrategyType.AnyOf,
        Approvers = approvers.Length > 0 ? approvers : ["alice@contoso.com"],
        RequestedAt = DateTimeOffset.UtcNow
    };

    /// <summary>
    /// Creates a resolved outcome correlated to the given escalation id. Carries the roster
    /// (as the service does on every resolution) so roster-gated resolved reads pass for
    /// <c>alice@contoso.com</c>; override <paramref name="approvers"/> to test denial paths.
    /// </summary>
    public static EscalationOutcome NewOutcome(Guid id, bool approved = true, params string[] approvers) => new()
    {
        EscalationId = id,
        IsApproved = approved,
        Approvers = approvers.Length > 0 ? approvers : ["alice@contoso.com"],
        Decisions =
        [
            new ApproverDecision
            {
                ApproverName = "alice@contoso.com",
                Approved = approved,
                Reason = approved ? "looks safe" : "too risky",
                RespondedAt = DateTimeOffset.UtcNow
            }
        ],
        ResolutionType = approved ? EscalationResolutionType.Approved : EscalationResolutionType.Denied,
        ResolvedAt = DateTimeOffset.UtcNow
    };
}
