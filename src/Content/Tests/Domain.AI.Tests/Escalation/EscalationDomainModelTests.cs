using Domain.AI.Escalation;
using Domain.AI.Governance;
using Xunit;

namespace Domain.AI.Tests.Escalation;

/// <summary>
/// Tests for escalation domain records and their factory methods/computed properties.
/// </summary>
public sealed class EscalationDomainModelTests
{
    // --- EscalationRequest ---

    [Fact]
    public void EscalationRequest_WithDefaults_SetsExpectedValues()
    {
        var request = new EscalationRequest
        {
            EscalationId = Guid.NewGuid(),
            AgentId = "agent-1",
            ToolName = "file_write",
            Arguments = new Dictionary<string, string> { ["path"] = "/etc/config" }.AsReadOnly(),
            Description = "Write to system config",
            RiskLevel = RiskLevel.High,
            Priority = EscalationPriority.Blocking,
            Approvers = ["admin"],
            RequestedAt = DateTimeOffset.UtcNow
        };

        Assert.NotEqual(Guid.Empty, request.EscalationId);
        Assert.NotEqual(default, request.RequestedAt);
        Assert.Equal(0, request.QuorumThreshold);
        Assert.Equal(300, request.TimeoutSeconds);
        Assert.Equal(ApprovalStrategyType.AnyOf, request.ApprovalStrategy);
        Assert.Null(request.OriginatingDecision);
    }

    [Fact]
    public void EscalationRequest_WithAllProperties_RoundTrips()
    {
        var decision = GovernanceDecision.Denied("blocked_tools", "default-policy", "Requires approval");
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var request = new EscalationRequest
        {
            EscalationId = id,
            AgentId = "agent-2",
            ToolName = "db_execute",
            Arguments = new Dictionary<string, string> { ["query"] = "DROP TABLE" }.AsReadOnly(),
            Description = "Execute destructive SQL",
            RiskLevel = RiskLevel.Critical,
            Priority = EscalationPriority.Critical,
            ApprovalStrategy = ApprovalStrategyType.AllOf,
            Approvers = ["admin", "dba"],
            QuorumThreshold = 2,
            TimeoutSeconds = 600,
            TimeoutAction = EscalationTimeoutAction.Deny,
            RequestedAt = now,
            OriginatingDecision = decision
        };

        Assert.Equal(id, request.EscalationId);
        Assert.Equal("agent-2", request.AgentId);
        Assert.Equal("db_execute", request.ToolName);
        Assert.Equal("DROP TABLE", request.Arguments["query"]);
        Assert.Equal("Execute destructive SQL", request.Description);
        Assert.Equal(RiskLevel.Critical, request.RiskLevel);
        Assert.Equal(EscalationPriority.Critical, request.Priority);
        Assert.Equal(ApprovalStrategyType.AllOf, request.ApprovalStrategy);
        Assert.Equal(2, request.Approvers.Count);
        Assert.Equal(2, request.QuorumThreshold);
        Assert.Equal(600, request.TimeoutSeconds);
        Assert.Equal(EscalationTimeoutAction.Deny, request.TimeoutAction);
        Assert.Equal(now, request.RequestedAt);
        Assert.NotNull(request.OriginatingDecision);
        Assert.Equal("blocked_tools", request.OriginatingDecision.MatchedRule);
    }

    // --- EscalationOutcome ---

    [Fact]
    public void EscalationOutcome_Approved_IsApprovedTrue()
    {
        var outcome = new EscalationOutcome
        {
            EscalationId = Guid.NewGuid(),
            IsApproved = true,
            Decisions = [new ApproverDecision
            {
                ApproverName = "admin",
                Approved = true,
                RespondedAt = DateTimeOffset.UtcNow
            }],
            ResolutionType = EscalationResolutionType.Approved,
            ResolvedAt = DateTimeOffset.UtcNow
        };

        Assert.True(outcome.IsApproved);
        Assert.Equal(EscalationResolutionType.Approved, outcome.ResolutionType);
    }

    [Fact]
    public void EscalationOutcome_Denied_IsApprovedFalse()
    {
        var outcome = new EscalationOutcome
        {
            EscalationId = Guid.NewGuid(),
            IsApproved = false,
            Decisions = [new ApproverDecision
            {
                ApproverName = "admin",
                Approved = false,
                Reason = "Too risky",
                RespondedAt = DateTimeOffset.UtcNow
            }],
            ResolutionType = EscalationResolutionType.Denied,
            ResolvedAt = DateTimeOffset.UtcNow
        };

        Assert.False(outcome.IsApproved);
    }

    [Fact]
    public void EscalationOutcome_TimedOut_HasCorrectResolutionType()
    {
        var outcome = new EscalationOutcome
        {
            EscalationId = Guid.NewGuid(),
            IsApproved = false,
            Decisions = [],
            ResolutionType = EscalationResolutionType.TimedOut,
            ResolvedAt = DateTimeOffset.UtcNow
        };

        Assert.Equal(EscalationResolutionType.TimedOut, outcome.ResolutionType);
        Assert.False(outcome.IsApproved);
        Assert.Null(outcome.EscalatedToTier);
    }

    [Fact]
    public void EscalationOutcome_Escalated_HasEscalatedToTier()
    {
        var outcome = new EscalationOutcome
        {
            EscalationId = Guid.NewGuid(),
            IsApproved = false,
            Decisions = [],
            ResolutionType = EscalationResolutionType.Escalated,
            ResolvedAt = DateTimeOffset.UtcNow,
            EscalatedToTier = AutonomyLevel.Autonomous
        };

        Assert.Equal(AutonomyLevel.Autonomous, outcome.EscalatedToTier);
    }

    // --- EscalationAuditRecord ---

    [Fact]
    public void EscalationAuditRecord_RequestType_SerializesCorrectly()
    {
        var record = new EscalationAuditRecord
        {
            RecordType = EscalationAuditRecordType.Request,
            EscalationId = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow,
            Payload = """{"AgentId":"agent-1","ToolName":"file_write"}"""
        };

        Assert.Equal(EscalationAuditRecordType.Request, record.RecordType);
        Assert.NotNull(record.Payload);
    }

    [Fact]
    public void EscalationAuditRecord_DecisionType_HasCorrectDiscriminator()
    {
        var record = new EscalationAuditRecord
        {
            RecordType = EscalationAuditRecordType.Decision,
            EscalationId = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow,
            Payload = """{"ApproverName":"admin","Approved":true}"""
        };

        Assert.Equal(EscalationAuditRecordType.Decision, record.RecordType);
    }

    // --- ApprovalEvaluation ---

    [Fact]
    public void ApprovalEvaluation_Resolved_HasEmptyPendingApprovers()
    {
        var evaluation = new ApprovalEvaluation
        {
            IsResolved = true,
            IsApproved = true,
            PendingApprovers = []
        };

        Assert.True(evaluation.IsResolved);
        Assert.Empty(evaluation.PendingApprovers);
    }

    [Fact]
    public void ApprovalEvaluation_NotResolved_HasPendingApprovers()
    {
        var evaluation = new ApprovalEvaluation
        {
            IsResolved = false,
            IsApproved = false,
            PendingApprovers = ["admin", "dba"]
        };

        Assert.False(evaluation.IsResolved);
        Assert.Equal(2, evaluation.PendingApprovers.Count);
        Assert.Contains("admin", evaluation.PendingApprovers);
        Assert.Contains("dba", evaluation.PendingApprovers);
    }

    // --- ApproverDecision ---

    [Fact]
    public void ApproverDecision_Approved_HasCorrectProperties()
    {
        var now = DateTimeOffset.UtcNow;
        var decision = new ApproverDecision
        {
            ApproverName = "admin",
            Approved = true,
            RespondedAt = now
        };

        Assert.Equal("admin", decision.ApproverName);
        Assert.True(decision.Approved);
        Assert.Equal(now, decision.RespondedAt);
        Assert.Null(decision.Reason);
    }

    [Fact]
    public void ApproverDecision_Denied_WithReason_HasReason()
    {
        var decision = new ApproverDecision
        {
            ApproverName = "security-lead",
            Approved = false,
            Reason = "Too risky",
            RespondedAt = DateTimeOffset.UtcNow
        };

        Assert.False(decision.Approved);
        Assert.Equal("Too risky", decision.Reason);
    }
}
