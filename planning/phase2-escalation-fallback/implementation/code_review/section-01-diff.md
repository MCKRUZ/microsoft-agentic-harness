diff --git a/src/Content/Domain/Domain.AI/Escalation/ApprovalEvaluation.cs b/src/Content/Domain/Domain.AI/Escalation/ApprovalEvaluation.cs
new file mode 100644
index 0000000..a27627c
--- /dev/null
+++ b/src/Content/Domain/Domain.AI/Escalation/ApprovalEvaluation.cs
@@ -0,0 +1,17 @@
+namespace Domain.AI.Escalation;
+
+/// <summary>
+/// Result of evaluating collected approver decisions against an approval strategy.
+/// Returned by <c>IApprovalStrategy.EvaluateDecision()</c>.
+/// </summary>
+public sealed record ApprovalEvaluation
+{
+    /// <summary>Whether enough decisions have been collected to resolve the escalation.</summary>
+    public required bool IsResolved { get; init; }
+
+    /// <summary>The approval verdict. Only meaningful when <see cref="IsResolved"/> is true.</summary>
+    public required bool IsApproved { get; init; }
+
+    /// <summary>Approvers who have not yet responded. Empty when fully resolved.</summary>
+    public required IReadOnlyList<string> PendingApprovers { get; init; }
+}
diff --git a/src/Content/Domain/Domain.AI/Escalation/ApprovalStrategyType.cs b/src/Content/Domain/Domain.AI/Escalation/ApprovalStrategyType.cs
new file mode 100644
index 0000000..d7acb8b
--- /dev/null
+++ b/src/Content/Domain/Domain.AI/Escalation/ApprovalStrategyType.cs
@@ -0,0 +1,15 @@
+namespace Domain.AI.Escalation;
+
+/// <summary>
+/// Strategy for evaluating multiple approver decisions.
+/// Used as the keyed DI discriminator for <c>IApprovalStrategy</c> resolution.
+/// </summary>
+public enum ApprovalStrategyType
+{
+    /// <summary>First approver response wins. Fastest resolution.</summary>
+    AnyOf,
+    /// <summary>All designated approvers must approve. A single denial immediately denies.</summary>
+    AllOf,
+    /// <summary>N-of-M approvers must agree. Requires <c>QuorumThreshold</c> on the request.</summary>
+    Quorum
+}
diff --git a/src/Content/Domain/Domain.AI/Escalation/ApproverDecision.cs b/src/Content/Domain/Domain.AI/Escalation/ApproverDecision.cs
new file mode 100644
index 0000000..59b6376
--- /dev/null
+++ b/src/Content/Domain/Domain.AI/Escalation/ApproverDecision.cs
@@ -0,0 +1,20 @@
+namespace Domain.AI.Escalation;
+
+/// <summary>
+/// A single approver's response to an escalation request.
+/// Collected by the escalation service and evaluated by the approval strategy.
+/// </summary>
+public sealed record ApproverDecision
+{
+    /// <summary>Identifier of the approver (user name, role, or service principal).</summary>
+    public required string ApproverName { get; init; }
+
+    /// <summary>Whether the approver granted approval.</summary>
+    public required bool Approved { get; init; }
+
+    /// <summary>Optional reason for the decision. Especially useful for denials.</summary>
+    public string? Reason { get; init; }
+
+    /// <summary>When the approver responded.</summary>
+    public required DateTimeOffset RespondedAt { get; init; }
+}
diff --git a/src/Content/Domain/Domain.AI/Escalation/EscalationAuditRecord.cs b/src/Content/Domain/Domain.AI/Escalation/EscalationAuditRecord.cs
new file mode 100644
index 0000000..aadfb6a
--- /dev/null
+++ b/src/Content/Domain/Domain.AI/Escalation/EscalationAuditRecord.cs
@@ -0,0 +1,24 @@
+namespace Domain.AI.Escalation;
+
+/// <summary>
+/// A single audit log entry for an escalation lifecycle event.
+/// Used by <c>IEscalationAuditStore</c> for append-only JSONL persistence.
+/// The <see cref="Payload"/> field contains the serialized event data,
+/// discriminated by <see cref="RecordType"/>.
+/// </summary>
+public sealed record EscalationAuditRecord
+{
+    /// <summary>Discriminator for deserialization of <see cref="Payload"/>.</summary>
+    public required EscalationAuditRecordType RecordType { get; init; }
+
+    /// <summary>Correlates to the originating escalation.</summary>
+    public required Guid EscalationId { get; init; }
+
+    /// <summary>When this audit record was created.</summary>
+    public required DateTimeOffset Timestamp { get; init; }
+
+    /// <summary>
+    /// Serialized JSON of the request, decision, or outcome depending on <see cref="RecordType"/>.
+    /// </summary>
+    public required string Payload { get; init; }
+}
diff --git a/src/Content/Domain/Domain.AI/Escalation/EscalationAuditRecordType.cs b/src/Content/Domain/Domain.AI/Escalation/EscalationAuditRecordType.cs
new file mode 100644
index 0000000..3166793
--- /dev/null
+++ b/src/Content/Domain/Domain.AI/Escalation/EscalationAuditRecordType.cs
@@ -0,0 +1,15 @@
+namespace Domain.AI.Escalation;
+
+/// <summary>
+/// Discriminator for <see cref="EscalationAuditRecord"/> entries.
+/// Determines how the <c>Payload</c> field should be deserialized.
+/// </summary>
+public enum EscalationAuditRecordType
+{
+    /// <summary>An escalation was requested.</summary>
+    Request,
+    /// <summary>An approver submitted a decision.</summary>
+    Decision,
+    /// <summary>The escalation was resolved (approved, denied, timed out, or escalated).</summary>
+    Outcome
+}
diff --git a/src/Content/Domain/Domain.AI/Escalation/EscalationOutcome.cs b/src/Content/Domain/Domain.AI/Escalation/EscalationOutcome.cs
new file mode 100644
index 0000000..1685c6d
--- /dev/null
+++ b/src/Content/Domain/Domain.AI/Escalation/EscalationOutcome.cs
@@ -0,0 +1,31 @@
+using Domain.AI.Governance;
+
+namespace Domain.AI.Escalation;
+
+/// <summary>
+/// The resolved result of an escalation request. Created when sufficient approver
+/// decisions have been collected, the request times out, or it is escalated.
+/// </summary>
+public sealed record EscalationOutcome
+{
+    /// <summary>Correlates back to the originating <see cref="EscalationRequest"/>.</summary>
+    public required Guid EscalationId { get; init; }
+
+    /// <summary>Final approval verdict.</summary>
+    public required bool IsApproved { get; init; }
+
+    /// <summary>Individual approver decisions collected during the escalation.</summary>
+    public required IReadOnlyList<ApproverDecision> Decisions { get; init; }
+
+    /// <summary>How the escalation was resolved.</summary>
+    public required EscalationResolutionType ResolutionType { get; init; }
+
+    /// <summary>When the escalation was resolved.</summary>
+    public required DateTimeOffset ResolvedAt { get; init; }
+
+    /// <summary>
+    /// If resolution was <see cref="EscalationResolutionType.Escalated"/>,
+    /// which authority tier received the escalated request. Null otherwise.
+    /// </summary>
+    public AutonomyLevel? EscalatedToTier { get; init; }
+}
diff --git a/src/Content/Domain/Domain.AI/Escalation/EscalationPriority.cs b/src/Content/Domain/Domain.AI/Escalation/EscalationPriority.cs
new file mode 100644
index 0000000..f60709e
--- /dev/null
+++ b/src/Content/Domain/Domain.AI/Escalation/EscalationPriority.cs
@@ -0,0 +1,15 @@
+namespace Domain.AI.Escalation;
+
+/// <summary>
+/// Urgency level of an escalation request. Higher values indicate greater urgency.
+/// Maps to <c>EscalationPriorityConfig</c> for per-priority timeout and notification settings.
+/// </summary>
+public enum EscalationPriority
+{
+    /// <summary>Non-blocking notification. Agent may continue other work.</summary>
+    Informational = 0,
+    /// <summary>Agent is blocked until the escalation resolves.</summary>
+    Blocking = 1,
+    /// <summary>Highest urgency. All approvers notified simultaneously regardless of strategy.</summary>
+    Critical = 2
+}
diff --git a/src/Content/Domain/Domain.AI/Escalation/EscalationRequest.cs b/src/Content/Domain/Domain.AI/Escalation/EscalationRequest.cs
new file mode 100644
index 0000000..d749ca0
--- /dev/null
+++ b/src/Content/Domain/Domain.AI/Escalation/EscalationRequest.cs
@@ -0,0 +1,56 @@
+using Domain.AI.Governance;
+
+namespace Domain.AI.Escalation;
+
+/// <summary>
+/// A structured request for human approval of an agent action that exceeds its authority.
+/// Built from a <see cref="GovernanceDecision"/> with <c>RequireApproval</c> action,
+/// or from an <see cref="AutonomyExceededResult"/> during delegation.
+/// </summary>
+public sealed record EscalationRequest
+{
+    /// <summary>Unique identifier for this escalation.</summary>
+    public required Guid EscalationId { get; init; }
+
+    /// <summary>The agent that attempted the action.</summary>
+    public required string AgentId { get; init; }
+
+    /// <summary>The tool or operation the agent tried to invoke.</summary>
+    public required string ToolName { get; init; }
+
+    /// <summary>Arguments passed to the tool (sanitized for audit display).</summary>
+    public required IReadOnlyDictionary<string, string> Arguments { get; init; }
+
+    /// <summary>Human-readable summary of the attempted action.</summary>
+    public required string Description { get; init; }
+
+    /// <summary>Risk level derived from the matched governance rule.</summary>
+    public required string RiskLevel { get; init; }
+
+    /// <summary>Urgency of this escalation, drives timeout and notification behavior.</summary>
+    public required EscalationPriority Priority { get; init; }
+
+    /// <summary>Strategy for evaluating multiple approver decisions.</summary>
+    public ApprovalStrategyType ApprovalStrategy { get; init; } = ApprovalStrategyType.AnyOf;
+
+    /// <summary>Ordered list of approver identifiers.</summary>
+    public required IReadOnlyList<string> Approvers { get; init; }
+
+    /// <summary>For Quorum strategy, the N in N-of-M required approvals.</summary>
+    public int QuorumThreshold { get; init; }
+
+    /// <summary>Seconds before this escalation expires.</summary>
+    public int TimeoutSeconds { get; init; } = 300;
+
+    /// <summary>Action to take when the escalation times out.</summary>
+    public EscalationTimeoutAction TimeoutAction { get; init; } = EscalationTimeoutAction.DenyAndEscalate;
+
+    /// <summary>When the escalation was created.</summary>
+    public required DateTimeOffset RequestedAt { get; init; }
+
+    /// <summary>
+    /// The governance decision that triggered this escalation. Null when triggered
+    /// by an <see cref="AutonomyExceededResult"/> from the supervisor.
+    /// </summary>
+    public GovernanceDecision? OriginatingDecision { get; init; }
+}
diff --git a/src/Content/Domain/Domain.AI/Escalation/EscalationResolutionType.cs b/src/Content/Domain/Domain.AI/Escalation/EscalationResolutionType.cs
new file mode 100644
index 0000000..690e6a5
--- /dev/null
+++ b/src/Content/Domain/Domain.AI/Escalation/EscalationResolutionType.cs
@@ -0,0 +1,16 @@
+namespace Domain.AI.Escalation;
+
+/// <summary>
+/// How an escalation was ultimately resolved. Used for audit records and OTel metric tags.
+/// </summary>
+public enum EscalationResolutionType
+{
+    /// <summary>Approved by sufficient approvers per the strategy.</summary>
+    Approved,
+    /// <summary>Denied by an approver or by strategy rules.</summary>
+    Denied,
+    /// <summary>No sufficient response within the timeout window.</summary>
+    TimedOut,
+    /// <summary>Forwarded to a higher authority tier.</summary>
+    Escalated
+}
diff --git a/src/Content/Domain/Domain.AI/Escalation/EscalationTimeoutAction.cs b/src/Content/Domain/Domain.AI/Escalation/EscalationTimeoutAction.cs
new file mode 100644
index 0000000..e6cc2ed
--- /dev/null
+++ b/src/Content/Domain/Domain.AI/Escalation/EscalationTimeoutAction.cs
@@ -0,0 +1,16 @@
+namespace Domain.AI.Escalation;
+
+/// <summary>
+/// Action taken when an escalation request expires without sufficient approver responses.
+/// </summary>
+public enum EscalationTimeoutAction
+{
+    /// <summary>Deny the action on timeout.</summary>
+    Deny,
+    /// <summary>Deny the action and escalate to a higher authority tier.</summary>
+    DenyAndEscalate,
+    /// <summary>Auto-approve the action on timeout (use with caution).</summary>
+    Approve,
+    /// <summary>Escalate to a higher authority tier without denying.</summary>
+    Escalate
+}
diff --git a/src/Content/Domain/Domain.AI/Escalation/EscalationWaitBehavior.cs b/src/Content/Domain/Domain.AI/Escalation/EscalationWaitBehavior.cs
new file mode 100644
index 0000000..0f15d2d
--- /dev/null
+++ b/src/Content/Domain/Domain.AI/Escalation/EscalationWaitBehavior.cs
@@ -0,0 +1,13 @@
+namespace Domain.AI.Escalation;
+
+/// <summary>
+/// Controls agent behavior while an escalation is pending.
+/// Configured per autonomy tier in <c>PermissionsConfig</c>.
+/// </summary>
+public enum EscalationWaitBehavior
+{
+    /// <summary>Agent pauses and awaits the escalation outcome before continuing.</summary>
+    Block,
+    /// <summary>Agent continues processing other work; escalation resolves asynchronously.</summary>
+    QueueAndContinue
+}
diff --git a/src/Content/Tests/Domain.AI.Tests/Escalation/EscalationDomainModelTests.cs b/src/Content/Tests/Domain.AI.Tests/Escalation/EscalationDomainModelTests.cs
new file mode 100644
index 0000000..870c41e
--- /dev/null
+++ b/src/Content/Tests/Domain.AI.Tests/Escalation/EscalationDomainModelTests.cs
@@ -0,0 +1,253 @@
+using Domain.AI.Escalation;
+using Domain.AI.Governance;
+using Xunit;
+
+namespace Domain.AI.Tests.Escalation;
+
+/// <summary>
+/// Tests for escalation domain records and their factory methods/computed properties.
+/// </summary>
+public sealed class EscalationDomainModelTests
+{
+    // --- EscalationRequest ---
+
+    [Fact]
+    public void EscalationRequest_WithDefaults_SetsExpectedValues()
+    {
+        var request = new EscalationRequest
+        {
+            EscalationId = Guid.NewGuid(),
+            AgentId = "agent-1",
+            ToolName = "file_write",
+            Arguments = new Dictionary<string, string> { ["path"] = "/etc/config" }.AsReadOnly(),
+            Description = "Write to system config",
+            RiskLevel = "High",
+            Priority = EscalationPriority.Blocking,
+            Approvers = ["admin"],
+            RequestedAt = DateTimeOffset.UtcNow
+        };
+
+        Assert.NotEqual(Guid.Empty, request.EscalationId);
+        Assert.NotEqual(default, request.RequestedAt);
+        Assert.Equal(0, request.QuorumThreshold);
+        Assert.Equal(300, request.TimeoutSeconds);
+        Assert.Equal(ApprovalStrategyType.AnyOf, request.ApprovalStrategy);
+        Assert.Null(request.OriginatingDecision);
+    }
+
+    [Fact]
+    public void EscalationRequest_WithAllProperties_RoundTrips()
+    {
+        var decision = GovernanceDecision.Denied("blocked_tools", "default-policy", "Requires approval");
+        var id = Guid.NewGuid();
+        var now = DateTimeOffset.UtcNow;
+
+        var request = new EscalationRequest
+        {
+            EscalationId = id,
+            AgentId = "agent-2",
+            ToolName = "db_execute",
+            Arguments = new Dictionary<string, string> { ["query"] = "DROP TABLE" }.AsReadOnly(),
+            Description = "Execute destructive SQL",
+            RiskLevel = "Critical",
+            Priority = EscalationPriority.Critical,
+            ApprovalStrategy = ApprovalStrategyType.AllOf,
+            Approvers = ["admin", "dba"],
+            QuorumThreshold = 2,
+            TimeoutSeconds = 600,
+            TimeoutAction = EscalationTimeoutAction.Deny,
+            RequestedAt = now,
+            OriginatingDecision = decision
+        };
+
+        Assert.Equal(id, request.EscalationId);
+        Assert.Equal("agent-2", request.AgentId);
+        Assert.Equal("db_execute", request.ToolName);
+        Assert.Equal("DROP TABLE", request.Arguments["query"]);
+        Assert.Equal("Execute destructive SQL", request.Description);
+        Assert.Equal("Critical", request.RiskLevel);
+        Assert.Equal(EscalationPriority.Critical, request.Priority);
+        Assert.Equal(ApprovalStrategyType.AllOf, request.ApprovalStrategy);
+        Assert.Equal(2, request.Approvers.Count);
+        Assert.Equal(2, request.QuorumThreshold);
+        Assert.Equal(600, request.TimeoutSeconds);
+        Assert.Equal(EscalationTimeoutAction.Deny, request.TimeoutAction);
+        Assert.Equal(now, request.RequestedAt);
+        Assert.NotNull(request.OriginatingDecision);
+        Assert.Equal("blocked_tools", request.OriginatingDecision.MatchedRule);
+    }
+
+    // --- EscalationOutcome ---
+
+    [Fact]
+    public void EscalationOutcome_Approved_IsApprovedTrue()
+    {
+        var outcome = new EscalationOutcome
+        {
+            EscalationId = Guid.NewGuid(),
+            IsApproved = true,
+            Decisions = [new ApproverDecision
+            {
+                ApproverName = "admin",
+                Approved = true,
+                RespondedAt = DateTimeOffset.UtcNow
+            }],
+            ResolutionType = EscalationResolutionType.Approved,
+            ResolvedAt = DateTimeOffset.UtcNow
+        };
+
+        Assert.True(outcome.IsApproved);
+        Assert.Equal(EscalationResolutionType.Approved, outcome.ResolutionType);
+    }
+
+    [Fact]
+    public void EscalationOutcome_Denied_IsApprovedFalse()
+    {
+        var outcome = new EscalationOutcome
+        {
+            EscalationId = Guid.NewGuid(),
+            IsApproved = false,
+            Decisions = [new ApproverDecision
+            {
+                ApproverName = "admin",
+                Approved = false,
+                Reason = "Too risky",
+                RespondedAt = DateTimeOffset.UtcNow
+            }],
+            ResolutionType = EscalationResolutionType.Denied,
+            ResolvedAt = DateTimeOffset.UtcNow
+        };
+
+        Assert.False(outcome.IsApproved);
+    }
+
+    [Fact]
+    public void EscalationOutcome_TimedOut_HasCorrectResolutionType()
+    {
+        var outcome = new EscalationOutcome
+        {
+            EscalationId = Guid.NewGuid(),
+            IsApproved = false,
+            Decisions = [],
+            ResolutionType = EscalationResolutionType.TimedOut,
+            ResolvedAt = DateTimeOffset.UtcNow
+        };
+
+        Assert.Equal(EscalationResolutionType.TimedOut, outcome.ResolutionType);
+        Assert.False(outcome.IsApproved);
+        Assert.Null(outcome.EscalatedToTier);
+    }
+
+    [Fact]
+    public void EscalationOutcome_Escalated_HasEscalatedToTier()
+    {
+        var outcome = new EscalationOutcome
+        {
+            EscalationId = Guid.NewGuid(),
+            IsApproved = false,
+            Decisions = [],
+            ResolutionType = EscalationResolutionType.Escalated,
+            ResolvedAt = DateTimeOffset.UtcNow,
+            EscalatedToTier = AutonomyLevel.Autonomous
+        };
+
+        Assert.Equal(AutonomyLevel.Autonomous, outcome.EscalatedToTier);
+    }
+
+    // --- EscalationAuditRecord ---
+
+    [Fact]
+    public void EscalationAuditRecord_RequestType_SerializesCorrectly()
+    {
+        var record = new EscalationAuditRecord
+        {
+            RecordType = EscalationAuditRecordType.Request,
+            EscalationId = Guid.NewGuid(),
+            Timestamp = DateTimeOffset.UtcNow,
+            Payload = """{"AgentId":"agent-1","ToolName":"file_write"}"""
+        };
+
+        Assert.Equal(EscalationAuditRecordType.Request, record.RecordType);
+        Assert.NotNull(record.Payload);
+    }
+
+    [Fact]
+    public void EscalationAuditRecord_DecisionType_HasCorrectDiscriminator()
+    {
+        var record = new EscalationAuditRecord
+        {
+            RecordType = EscalationAuditRecordType.Decision,
+            EscalationId = Guid.NewGuid(),
+            Timestamp = DateTimeOffset.UtcNow,
+            Payload = """{"ApproverName":"admin","Approved":true}"""
+        };
+
+        Assert.Equal(EscalationAuditRecordType.Decision, record.RecordType);
+    }
+
+    // --- ApprovalEvaluation ---
+
+    [Fact]
+    public void ApprovalEvaluation_Resolved_HasEmptyPendingApprovers()
+    {
+        var evaluation = new ApprovalEvaluation
+        {
+            IsResolved = true,
+            IsApproved = true,
+            PendingApprovers = []
+        };
+
+        Assert.True(evaluation.IsResolved);
+        Assert.Empty(evaluation.PendingApprovers);
+    }
+
+    [Fact]
+    public void ApprovalEvaluation_NotResolved_HasPendingApprovers()
+    {
+        var evaluation = new ApprovalEvaluation
+        {
+            IsResolved = false,
+            IsApproved = false,
+            PendingApprovers = ["admin", "dba"]
+        };
+
+        Assert.False(evaluation.IsResolved);
+        Assert.Equal(2, evaluation.PendingApprovers.Count);
+        Assert.Contains("admin", evaluation.PendingApprovers);
+        Assert.Contains("dba", evaluation.PendingApprovers);
+    }
+
+    // --- ApproverDecision ---
+
+    [Fact]
+    public void ApproverDecision_Approved_HasCorrectProperties()
+    {
+        var now = DateTimeOffset.UtcNow;
+        var decision = new ApproverDecision
+        {
+            ApproverName = "admin",
+            Approved = true,
+            RespondedAt = now
+        };
+
+        Assert.Equal("admin", decision.ApproverName);
+        Assert.True(decision.Approved);
+        Assert.Equal(now, decision.RespondedAt);
+        Assert.Null(decision.Reason);
+    }
+
+    [Fact]
+    public void ApproverDecision_Denied_WithReason_HasReason()
+    {
+        var decision = new ApproverDecision
+        {
+            ApproverName = "security-lead",
+            Approved = false,
+            Reason = "Too risky",
+            RespondedAt = DateTimeOffset.UtcNow
+        };
+
+        Assert.False(decision.Approved);
+        Assert.Equal("Too risky", decision.Reason);
+    }
+}
