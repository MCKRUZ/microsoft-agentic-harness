diff --git a/src/Content/Application/Application.AI.Common/Interfaces/Escalation/IApprovalStrategy.cs b/src/Content/Application/Application.AI.Common/Interfaces/Escalation/IApprovalStrategy.cs
new file mode 100644
index 0000000..a57e9f5
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/Interfaces/Escalation/IApprovalStrategy.cs
@@ -0,0 +1,33 @@
+using Domain.AI.Escalation;
+
+namespace Application.AI.Common.Interfaces.Escalation;
+
+/// <summary>
+/// Evaluates approver decisions against an escalation request to determine resolution.
+/// Registered via keyed DI -- resolved by <see cref="ApprovalStrategyType"/>.
+/// </summary>
+/// <remarks>
+/// <para>Three built-in strategies:</para>
+/// <list type="bullet">
+///   <item><c>AnyOf</c> -- first response wins (approve or deny)</item>
+///   <item><c>AllOf</c> -- unanimous approval required, single denial resolves immediately</item>
+///   <item><c>Quorum</c> -- N-of-M threshold, resolved when outcome is mathematically determined</item>
+/// </list>
+/// </remarks>
+public interface IApprovalStrategy
+{
+    /// <summary>
+    /// Evaluates collected decisions against the request's approval requirements.
+    /// </summary>
+    /// <param name="request">The escalation request containing approver list and threshold config.</param>
+    /// <param name="decisions">All decisions collected so far.</param>
+    /// <returns>Evaluation result indicating whether the escalation is resolved and the verdict.</returns>
+    ApprovalEvaluation EvaluateDecision(
+        EscalationRequest request,
+        IReadOnlyList<ApproverDecision> decisions);
+
+    /// <summary>
+    /// The strategy type this implementation handles. Used as the keyed DI key.
+    /// </summary>
+    ApprovalStrategyType StrategyType { get; }
+}
diff --git a/src/Content/Application/Application.Core/Escalation/Strategies/AllOfApprovalStrategy.cs b/src/Content/Application/Application.Core/Escalation/Strategies/AllOfApprovalStrategy.cs
new file mode 100644
index 0000000..36cdac2
--- /dev/null
+++ b/src/Content/Application/Application.Core/Escalation/Strategies/AllOfApprovalStrategy.cs
@@ -0,0 +1,40 @@
+using Application.AI.Common.Interfaces.Escalation;
+using Domain.AI.Escalation;
+
+namespace Application.Core.Escalation.Strategies;
+
+/// <summary>
+/// Unanimous approval required. A single denial resolves the escalation as denied immediately.
+/// </summary>
+public sealed class AllOfApprovalStrategy : IApprovalStrategy
+{
+    /// <inheritdoc />
+    public ApprovalStrategyType StrategyType => ApprovalStrategyType.AllOf;
+
+    /// <inheritdoc />
+    public ApprovalEvaluation EvaluateDecision(
+        EscalationRequest request,
+        IReadOnlyList<ApproverDecision> decisions)
+    {
+        var respondedNames = decisions.Select(d => d.ApproverName).ToHashSet(StringComparer.OrdinalIgnoreCase);
+        var pending = request.Approvers.Where(a => !respondedNames.Contains(a)).ToList();
+
+        if (decisions.Any(d => !d.Approved))
+        {
+            return new ApprovalEvaluation
+            {
+                IsResolved = true,
+                IsApproved = false,
+                PendingApprovers = pending
+            };
+        }
+
+        var allResponded = decisions.Count >= request.Approvers.Count;
+        return new ApprovalEvaluation
+        {
+            IsResolved = allResponded,
+            IsApproved = allResponded,
+            PendingApprovers = pending
+        };
+    }
+}
diff --git a/src/Content/Application/Application.Core/Escalation/Strategies/AnyOfApprovalStrategy.cs b/src/Content/Application/Application.Core/Escalation/Strategies/AnyOfApprovalStrategy.cs
new file mode 100644
index 0000000..1e67fc5
--- /dev/null
+++ b/src/Content/Application/Application.Core/Escalation/Strategies/AnyOfApprovalStrategy.cs
@@ -0,0 +1,40 @@
+using Application.AI.Common.Interfaces.Escalation;
+using Domain.AI.Escalation;
+
+namespace Application.Core.Escalation.Strategies;
+
+/// <summary>
+/// First response wins -- any single approval or denial resolves the escalation immediately.
+/// </summary>
+public sealed class AnyOfApprovalStrategy : IApprovalStrategy
+{
+    /// <inheritdoc />
+    public ApprovalStrategyType StrategyType => ApprovalStrategyType.AnyOf;
+
+    /// <inheritdoc />
+    public ApprovalEvaluation EvaluateDecision(
+        EscalationRequest request,
+        IReadOnlyList<ApproverDecision> decisions)
+    {
+        var respondedNames = decisions.Select(d => d.ApproverName).ToHashSet(StringComparer.OrdinalIgnoreCase);
+        var pending = request.Approvers.Where(a => !respondedNames.Contains(a)).ToList();
+
+        if (decisions.Count == 0)
+        {
+            return new ApprovalEvaluation
+            {
+                IsResolved = false,
+                IsApproved = false,
+                PendingApprovers = pending
+            };
+        }
+
+        var firstDecision = decisions[0];
+        return new ApprovalEvaluation
+        {
+            IsResolved = true,
+            IsApproved = firstDecision.Approved,
+            PendingApprovers = pending
+        };
+    }
+}
diff --git a/src/Content/Application/Application.Core/Escalation/Strategies/QuorumApprovalStrategy.cs b/src/Content/Application/Application.Core/Escalation/Strategies/QuorumApprovalStrategy.cs
new file mode 100644
index 0000000..71114a5
--- /dev/null
+++ b/src/Content/Application/Application.Core/Escalation/Strategies/QuorumApprovalStrategy.cs
@@ -0,0 +1,56 @@
+using Application.AI.Common.Interfaces.Escalation;
+using Domain.AI.Escalation;
+
+namespace Application.Core.Escalation.Strategies;
+
+/// <summary>
+/// N-of-M threshold approval. Resolves as soon as the outcome is mathematically determined --
+/// either enough approvals to meet quorum, or enough denials to make quorum impossible.
+/// </summary>
+public sealed class QuorumApprovalStrategy : IApprovalStrategy
+{
+    /// <inheritdoc />
+    public ApprovalStrategyType StrategyType => ApprovalStrategyType.Quorum;
+
+    /// <inheritdoc />
+    public ApprovalEvaluation EvaluateDecision(
+        EscalationRequest request,
+        IReadOnlyList<ApproverDecision> decisions)
+    {
+        var respondedNames = decisions.Select(d => d.ApproverName).ToHashSet(StringComparer.OrdinalIgnoreCase);
+        var pending = request.Approvers.Where(a => !respondedNames.Contains(a)).ToList();
+
+        var approvedCount = decisions.Count(d => d.Approved);
+        var deniedCount = decisions.Count(d => !d.Approved);
+        var totalApprovers = request.Approvers.Count;
+        var quorumThreshold = request.QuorumThreshold;
+
+        if (approvedCount >= quorumThreshold)
+        {
+            return new ApprovalEvaluation
+            {
+                IsResolved = true,
+                IsApproved = true,
+                PendingApprovers = pending
+            };
+        }
+
+        var remainingVotes = totalApprovers - approvedCount - deniedCount;
+        if (approvedCount + remainingVotes < quorumThreshold)
+        {
+            return new ApprovalEvaluation
+            {
+                IsResolved = true,
+                IsApproved = false,
+                PendingApprovers = pending
+            };
+        }
+
+        return new ApprovalEvaluation
+        {
+            IsResolved = false,
+            IsApproved = false,
+            PendingApprovers = pending
+        };
+    }
+}
diff --git a/src/Content/Tests/Application.Core.Tests/Escalation/Strategies/AllOfApprovalStrategyTests.cs b/src/Content/Tests/Application.Core.Tests/Escalation/Strategies/AllOfApprovalStrategyTests.cs
new file mode 100644
index 0000000..8e55d7c
--- /dev/null
+++ b/src/Content/Tests/Application.Core.Tests/Escalation/Strategies/AllOfApprovalStrategyTests.cs
@@ -0,0 +1,96 @@
+using Application.AI.Common.Interfaces.Escalation;
+using Application.Core.Escalation.Strategies;
+using Domain.AI.Escalation;
+using FluentAssertions;
+using Xunit;
+
+namespace Application.Core.Tests.Escalation.Strategies;
+
+public class AllOfApprovalStrategyTests
+{
+    private readonly IApprovalStrategy _sut = new AllOfApprovalStrategy();
+
+    private static EscalationRequest CreateRequest(params string[] approvers) => new()
+    {
+        EscalationId = Guid.NewGuid(),
+        AgentId = "test-agent",
+        ToolName = "test-tool",
+        Arguments = new Dictionary<string, string>(),
+        Description = "Test escalation",
+        RiskLevel = RiskLevel.Medium,
+        Priority = EscalationPriority.Blocking,
+        ApprovalStrategy = ApprovalStrategyType.AllOf,
+        Approvers = approvers,
+        RequestedAt = DateTimeOffset.UtcNow
+    };
+
+    private static ApproverDecision Approve(string name) => new()
+    {
+        ApproverName = name,
+        Approved = true,
+        RespondedAt = DateTimeOffset.UtcNow
+    };
+
+    private static ApproverDecision Deny(string name) => new()
+    {
+        ApproverName = name,
+        Approved = false,
+        Reason = "Denied",
+        RespondedAt = DateTimeOffset.UtcNow
+    };
+
+    [Fact]
+    public void EvaluateDecision_AllApproved_ResolvesApproved()
+    {
+        var request = CreateRequest("alice", "bob", "carol");
+        var decisions = new[] { Approve("alice"), Approve("bob"), Approve("carol") };
+
+        var result = _sut.EvaluateDecision(request, decisions);
+
+        result.IsResolved.Should().BeTrue();
+        result.IsApproved.Should().BeTrue();
+        result.PendingApprovers.Should().BeEmpty();
+    }
+
+    [Fact]
+    public void EvaluateDecision_SingleDenialAmongMultiple_ResolvesDeniedImmediately()
+    {
+        var request = CreateRequest("alice", "bob", "carol");
+        var decisions = new[] { Approve("alice"), Deny("bob") };
+
+        var result = _sut.EvaluateDecision(request, decisions);
+
+        result.IsResolved.Should().BeTrue();
+        result.IsApproved.Should().BeFalse();
+    }
+
+    [Fact]
+    public void EvaluateDecision_PartialApprovals_NotResolved()
+    {
+        var request = CreateRequest("alice", "bob", "carol");
+        var decisions = new[] { Approve("alice"), Approve("bob") };
+
+        var result = _sut.EvaluateDecision(request, decisions);
+
+        result.IsResolved.Should().BeFalse();
+        result.PendingApprovers.Should().BeEquivalentTo(["carol"]);
+    }
+
+    [Fact]
+    public void EvaluateDecision_SingleApprover_ApprovesImmediately()
+    {
+        var request = CreateRequest("alice");
+        var decisions = new[] { Approve("alice") };
+
+        var result = _sut.EvaluateDecision(request, decisions);
+
+        result.IsResolved.Should().BeTrue();
+        result.IsApproved.Should().BeTrue();
+    }
+
+    [Fact]
+    public void StrategyType_ReturnsAllOf()
+    {
+        _sut.StrategyType.Should().Be(ApprovalStrategyType.AllOf);
+    }
+}
diff --git a/src/Content/Tests/Application.Core.Tests/Escalation/Strategies/AnyOfApprovalStrategyTests.cs b/src/Content/Tests/Application.Core.Tests/Escalation/Strategies/AnyOfApprovalStrategyTests.cs
new file mode 100644
index 0000000..c1e3ae6
--- /dev/null
+++ b/src/Content/Tests/Application.Core.Tests/Escalation/Strategies/AnyOfApprovalStrategyTests.cs
@@ -0,0 +1,96 @@
+using Application.AI.Common.Interfaces.Escalation;
+using Application.Core.Escalation.Strategies;
+using Domain.AI.Escalation;
+using FluentAssertions;
+using Xunit;
+
+namespace Application.Core.Tests.Escalation.Strategies;
+
+public class AnyOfApprovalStrategyTests
+{
+    private readonly IApprovalStrategy _sut = new AnyOfApprovalStrategy();
+
+    private static EscalationRequest CreateRequest(params string[] approvers) => new()
+    {
+        EscalationId = Guid.NewGuid(),
+        AgentId = "test-agent",
+        ToolName = "test-tool",
+        Arguments = new Dictionary<string, string>(),
+        Description = "Test escalation",
+        RiskLevel = RiskLevel.Medium,
+        Priority = EscalationPriority.Blocking,
+        ApprovalStrategy = ApprovalStrategyType.AnyOf,
+        Approvers = approvers,
+        RequestedAt = DateTimeOffset.UtcNow
+    };
+
+    private static ApproverDecision Approve(string name) => new()
+    {
+        ApproverName = name,
+        Approved = true,
+        RespondedAt = DateTimeOffset.UtcNow
+    };
+
+    private static ApproverDecision Deny(string name) => new()
+    {
+        ApproverName = name,
+        Approved = false,
+        Reason = "Denied",
+        RespondedAt = DateTimeOffset.UtcNow
+    };
+
+    [Fact]
+    public void EvaluateDecision_SingleApproval_ResolvesApproved()
+    {
+        var request = CreateRequest("alice", "bob", "carol");
+        var decisions = new[] { Approve("alice") };
+
+        var result = _sut.EvaluateDecision(request, decisions);
+
+        result.IsResolved.Should().BeTrue();
+        result.IsApproved.Should().BeTrue();
+        result.PendingApprovers.Should().BeEquivalentTo(["bob", "carol"]);
+    }
+
+    [Fact]
+    public void EvaluateDecision_SingleDenial_ResolvesDenied()
+    {
+        var request = CreateRequest("alice", "bob", "carol");
+        var decisions = new[] { Deny("bob") };
+
+        var result = _sut.EvaluateDecision(request, decisions);
+
+        result.IsResolved.Should().BeTrue();
+        result.IsApproved.Should().BeFalse();
+    }
+
+    [Fact]
+    public void EvaluateDecision_NoDecisions_NotResolved()
+    {
+        var request = CreateRequest("alice", "bob", "carol");
+
+        var result = _sut.EvaluateDecision(request, Array.Empty<ApproverDecision>());
+
+        result.IsResolved.Should().BeFalse();
+        result.IsApproved.Should().BeFalse();
+        result.PendingApprovers.Should().BeEquivalentTo(["alice", "bob", "carol"]);
+    }
+
+    [Fact]
+    public void EvaluateDecision_MultipleApprovers_FirstResponseWins()
+    {
+        var request = CreateRequest("alice", "bob", "carol");
+        var decisions = new[] { Approve("alice"), Deny("bob") };
+
+        var result = _sut.EvaluateDecision(request, decisions);
+
+        result.IsResolved.Should().BeTrue();
+        result.IsApproved.Should().BeTrue();
+    }
+
+    [Fact]
+    public void StrategyType_ReturnsAnyOf()
+    {
+        _sut.StrategyType.Should().Be(ApprovalStrategyType.AnyOf);
+    }
+}
diff --git a/src/Content/Tests/Application.Core.Tests/Escalation/Strategies/QuorumApprovalStrategyTests.cs b/src/Content/Tests/Application.Core.Tests/Escalation/Strategies/QuorumApprovalStrategyTests.cs
new file mode 100644
index 0000000..93b143b
--- /dev/null
+++ b/src/Content/Tests/Application.Core.Tests/Escalation/Strategies/QuorumApprovalStrategyTests.cs
@@ -0,0 +1,141 @@
+using Application.AI.Common.Interfaces.Escalation;
+using Application.Core.Escalation.Strategies;
+using Domain.AI.Escalation;
+using FluentAssertions;
+using Xunit;
+
+namespace Application.Core.Tests.Escalation.Strategies;
+
+public class QuorumApprovalStrategyTests
+{
+    private readonly IApprovalStrategy _sut = new QuorumApprovalStrategy();
+
+    private static EscalationRequest CreateRequest(string[] approvers, int quorumThreshold) => new()
+    {
+        EscalationId = Guid.NewGuid(),
+        AgentId = "test-agent",
+        ToolName = "test-tool",
+        Arguments = new Dictionary<string, string>(),
+        Description = "Test escalation",
+        RiskLevel = RiskLevel.Medium,
+        Priority = EscalationPriority.Blocking,
+        ApprovalStrategy = ApprovalStrategyType.Quorum,
+        Approvers = approvers,
+        QuorumThreshold = quorumThreshold,
+        RequestedAt = DateTimeOffset.UtcNow
+    };
+
+    private static ApproverDecision Approve(string name) => new()
+    {
+        ApproverName = name,
+        Approved = true,
+        RespondedAt = DateTimeOffset.UtcNow
+    };
+
+    private static ApproverDecision Deny(string name) => new()
+    {
+        ApproverName = name,
+        Approved = false,
+        Reason = "Denied",
+        RespondedAt = DateTimeOffset.UtcNow
+    };
+
+    [Fact]
+    public void EvaluateDecision_QuorumMet_ResolvesApproved()
+    {
+        var request = CreateRequest(["alice", "bob", "carol"], quorumThreshold: 2);
+        var decisions = new[] { Approve("alice"), Approve("bob") };
+
+        var result = _sut.EvaluateDecision(request, decisions);
+
+        result.IsResolved.Should().BeTrue();
+        result.IsApproved.Should().BeTrue();
+    }
+
+    [Fact]
+    public void EvaluateDecision_QuorumImpossible_ResolvesDenied()
+    {
+        var request = CreateRequest(["alice", "bob", "carol"], quorumThreshold: 2);
+        var decisions = new[] { Deny("alice"), Deny("bob") };
+
+        var result = _sut.EvaluateDecision(request, decisions);
+
+        result.IsResolved.Should().BeTrue();
+        result.IsApproved.Should().BeFalse();
+    }
+
+    [Fact]
+    public void EvaluateDecision_InsufficientVotes_NotResolved()
+    {
+        var request = CreateRequest(["alice", "bob", "carol"], quorumThreshold: 2);
+        var decisions = new[] { Approve("alice") };
+
+        var result = _sut.EvaluateDecision(request, decisions);
+
+        result.IsResolved.Should().BeFalse();
+    }
+
+    [Fact]
+    public void EvaluateDecision_EdgeCase_OneOfOne_ResolvesOnFirst()
+    {
+        var request = CreateRequest(["alice"], quorumThreshold: 1);
+        var decisions = new[] { Approve("alice") };
+
+        var result = _sut.EvaluateDecision(request, decisions);
+
+        result.IsResolved.Should().BeTrue();
+        result.IsApproved.Should().BeTrue();
+    }
+
+    [Theory]
+    [InlineData(true, false, false)]
+    [InlineData(true, true, true)]
+    public void EvaluateDecision_TwoOfThree_MixedOutcomes(
+        bool firstApproves, bool secondApproves, bool expectedResolved)
+    {
+        var request = CreateRequest(["alice", "bob", "carol"], quorumThreshold: 2);
+        var decisions = new[]
+        {
+            firstApproves ? Approve("alice") : Deny("alice"),
+            secondApproves ? Approve("bob") : Deny("bob")
+        };
+
+        var result = _sut.EvaluateDecision(request, decisions);
+
+        result.IsResolved.Should().Be(expectedResolved);
+    }
+
+    [Fact]
+    public void EvaluateDecision_TwoOfThree_TwoDenials_ResolvesDenied()
+    {
+        var request = CreateRequest(["alice", "bob", "carol"], quorumThreshold: 2);
+        var decisions = new[] { Deny("alice"), Deny("bob") };
+
+        var result = _sut.EvaluateDecision(request, decisions);
+
+        result.IsResolved.Should().BeTrue();
+        result.IsApproved.Should().BeFalse();
+    }
+
+    [Fact]
+    public void EvaluateDecision_ThresholdEqualsTotal_BehavesLikeAllOf()
+    {
+        var request = CreateRequest(["alice", "bob", "carol"], quorumThreshold: 3);
+
+        var allApproved = _sut.EvaluateDecision(request,
+            [Approve("alice"), Approve("bob"), Approve("carol")]);
+        allApproved.IsResolved.Should().BeTrue();
+        allApproved.IsApproved.Should().BeTrue();
+
+        var oneDenied = _sut.EvaluateDecision(request,
+            [Approve("alice"), Deny("bob")]);
+        oneDenied.IsResolved.Should().BeTrue();
+        oneDenied.IsApproved.Should().BeFalse();
+    }
+
+    [Fact]
+    public void StrategyType_ReturnsQuorum()
+    {
+        _sut.StrategyType.Should().Be(ApprovalStrategyType.Quorum);
+    }
+}
