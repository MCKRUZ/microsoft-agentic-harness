diff --git a/src/Content/Application/Application.AI.Common/MediatRBehaviors/GovernancePolicyBehavior.cs b/src/Content/Application/Application.AI.Common/MediatRBehaviors/GovernancePolicyBehavior.cs
index 36fb391..46157ef 100644
--- a/src/Content/Application/Application.AI.Common/MediatRBehaviors/GovernancePolicyBehavior.cs
+++ b/src/Content/Application/Application.AI.Common/MediatRBehaviors/GovernancePolicyBehavior.cs
@@ -1,8 +1,12 @@
 using Application.AI.Common.Interfaces.Agent;
+using Application.AI.Common.Interfaces.Escalation;
 using Application.AI.Common.Interfaces.Governance;
 using Application.AI.Common.Interfaces.MediatR;
+using Domain.AI.Escalation;
+using Domain.AI.Governance;
 using Domain.Common;
 using Domain.Common.Config.AI;
+using Domain.Common.Config.AI.Permissions;
 using Domain.Common.Helpers;
 using MediatR;
 using Microsoft.Extensions.Logging;
@@ -26,20 +30,26 @@ public sealed class GovernancePolicyBehavior<TRequest, TResponse>
     private readonly IGovernanceAuditService _auditService;
     private readonly IAgentExecutionContext _executionContext;
     private readonly IOptionsMonitor<GovernanceConfig> _config;
+    private readonly IOptionsMonitor<PermissionsConfig> _permissionsConfig;
     private readonly ILogger<GovernancePolicyBehavior<TRequest, TResponse>> _logger;
+    private readonly IEscalationService? _escalationService;
 
     public GovernancePolicyBehavior(
         IGovernancePolicyEngine policyEngine,
         IGovernanceAuditService auditService,
         IAgentExecutionContext executionContext,
         IOptionsMonitor<GovernanceConfig> config,
-        ILogger<GovernancePolicyBehavior<TRequest, TResponse>> logger)
+        IOptionsMonitor<PermissionsConfig> permissionsConfig,
+        ILogger<GovernancePolicyBehavior<TRequest, TResponse>> logger,
+        IEscalationService? escalationService = null)
     {
         _policyEngine = policyEngine;
         _auditService = auditService;
         _executionContext = executionContext;
         _config = config;
+        _permissionsConfig = permissionsConfig;
         _logger = logger;
+        _escalationService = escalationService;
     }
 
     public async Task<TResponse> Handle(
@@ -63,6 +73,9 @@ public sealed class GovernancePolicyBehavior<TRequest, TResponse>
         if (decision.IsAllowed)
             return await next();
 
+        if (decision.Action == GovernancePolicyAction.RequireApproval)
+            return await HandleRequireApprovalAsync(agentId, toolRequest, decision, next, cancellationToken);
+
         _logger.LogWarning(
             "Governance policy denied agent {AgentId} access to tool {ToolName}: {Reason} (rule: {Rule})",
             agentId, toolRequest.ToolName, decision.Reason, decision.MatchedRule);
@@ -72,4 +85,100 @@ public sealed class GovernancePolicyBehavior<TRequest, TResponse>
 
         throw new InvalidOperationException($"Governance policy denied: {decision.Reason}");
     }
+
+    private async Task<TResponse> HandleRequireApprovalAsync(
+        string agentId,
+        IToolRequest toolRequest,
+        GovernanceDecision decision,
+        RequestHandlerDelegate<TResponse> next,
+        CancellationToken cancellationToken)
+    {
+        var escalationConfig = _config.CurrentValue.Escalation;
+
+        if (escalationConfig?.Enabled != true || _escalationService is null)
+        {
+            _logger.LogWarning(
+                "Escalation disabled — treating RequireApproval as denial for agent {AgentId} tool {ToolName}",
+                agentId, toolRequest.ToolName);
+
+            if (ResultHelper.TryCreateFailure<TResponse>(nameof(Result.GovernanceBlocked), decision.Reason, out var denied))
+                return denied;
+
+            throw new InvalidOperationException($"Governance policy denied: {decision.Reason}");
+        }
+
+        var escalationRequest = new EscalationRequest
+        {
+            EscalationId = Guid.NewGuid(),
+            AgentId = agentId,
+            ToolName = toolRequest.ToolName,
+            Arguments = new Dictionary<string, string>(),
+            Description = $"Agent '{agentId}' requires approval to invoke '{toolRequest.ToolName}': {decision.Reason}",
+            RiskLevel = RiskLevel.Medium,
+            Priority = EscalationPriority.Blocking,
+            ApprovalStrategy = Enum.TryParse<ApprovalStrategyType>(escalationConfig.DefaultApprovalStrategy, true, out var strategy)
+                ? strategy
+                : ApprovalStrategyType.AnyOf,
+            Approvers = decision.Approvers ?? [],
+            QuorumThreshold = 1,
+            TimeoutSeconds = escalationConfig.DefaultTimeoutSeconds,
+            TimeoutAction = Enum.TryParse<EscalationTimeoutAction>(escalationConfig.DefaultTimeoutAction, true, out var timeoutAction)
+                ? timeoutAction
+                : EscalationTimeoutAction.DenyAndEscalate,
+            RequestedAt = DateTimeOffset.UtcNow,
+            OriginatingDecision = decision
+        };
+
+        var waitBehavior = ResolveEscalationWaitBehavior();
+
+        if (waitBehavior == EscalationWaitBehavior.QueueAndContinue)
+        {
+            var escalationId = await _escalationService.QueueEscalationAsync(escalationRequest, cancellationToken);
+
+            _logger.LogInformation(
+                "Queued escalation {EscalationId} for agent {AgentId} tool {ToolName} (QueueAndContinue)",
+                escalationId, agentId, toolRequest.ToolName);
+
+            if (ResultHelper.TryCreateFailure<TResponse>(nameof(Result.PendingApproval),
+                    $"Escalation {escalationId} pending approval", out var pending))
+                return pending;
+
+            throw new InvalidOperationException($"Escalation {escalationId} pending approval");
+        }
+
+        // Block wait behavior — await the escalation outcome
+        var outcome = await _escalationService.RequestEscalationAsync(escalationRequest, cancellationToken);
+
+        if (outcome.IsApproved)
+        {
+            _logger.LogInformation(
+                "Escalation {EscalationId} approved for agent {AgentId} tool {ToolName}",
+                outcome.EscalationId, agentId, toolRequest.ToolName);
+            return await next();
+        }
+
+        _logger.LogWarning(
+            "Escalation {EscalationId} denied for agent {AgentId} tool {ToolName}",
+            outcome.EscalationId, agentId, toolRequest.ToolName);
+
+        if (ResultHelper.TryCreateFailure<TResponse>(nameof(Result.GovernanceBlocked),
+                $"Escalation denied: {decision.Reason}", out var blocked))
+            return blocked;
+
+        throw new InvalidOperationException($"Escalation denied: {decision.Reason}");
+    }
+
+    private EscalationWaitBehavior ResolveEscalationWaitBehavior()
+    {
+        var tierPolicies = _permissionsConfig.CurrentValue.TierPolicies;
+
+        // Check configured tier policies for escalation behavior
+        foreach (var policy in tierPolicies.Values)
+        {
+            if (Enum.TryParse<EscalationWaitBehavior>(policy.EscalationBehavior, true, out var behavior))
+                return behavior;
+        }
+
+        return EscalationWaitBehavior.Block;
+    }
 }
diff --git a/src/Content/Application/Application.Core/Workflows/Governance/CreateApprovalRequestExecutor.cs b/src/Content/Application/Application.Core/Workflows/Governance/CreateApprovalRequestExecutor.cs
index 17df525..0db57aa 100644
--- a/src/Content/Application/Application.Core/Workflows/Governance/CreateApprovalRequestExecutor.cs
+++ b/src/Content/Application/Application.Core/Workflows/Governance/CreateApprovalRequestExecutor.cs
@@ -1,4 +1,6 @@
+using Application.AI.Common.Interfaces.Escalation;
 using Application.AI.Common.Interfaces.Governance;
+using Domain.AI.Escalation;
 using Microsoft.Agents.AI.Workflows;
 
 namespace Application.Core.Workflows.Governance;
@@ -6,21 +8,25 @@ namespace Application.Core.Workflows.Governance;
 /// <summary>
 /// Transforms a <see cref="GovernanceApprovalInput"/> into a human-readable
 /// <see cref="ApprovalRequest"/> and logs the pending approval to the governance audit chain.
+/// When an <see cref="IEscalationService"/> is wired, also queues an escalation request
+/// to start the notification dispatch and timeout timer.
 /// First step in the governance approval workflow.
 /// </summary>
 public sealed class CreateApprovalRequestExecutor(
-    IGovernanceAuditService auditService)
+    IGovernanceAuditService auditService,
+    IEscalationService? escalationService = null)
     : Executor<GovernanceApprovalInput, ApprovalRequest>("CreateApprovalRequest")
 {
     /// <summary>
     /// Builds an <see cref="ApprovalRequest"/> from the governance decision metadata
-    /// and records the pending approval in the audit trail.
+    /// and records the pending approval in the audit trail. When escalation is available,
+    /// queues a non-blocking escalation to start notification dispatch.
     /// </summary>
     /// <param name="message">The governance input containing the tool call details and initial decision.</param>
     /// <param name="context">The MAF workflow context.</param>
     /// <param name="cancellationToken">Cancellation token.</param>
     /// <returns>An <see cref="ApprovalRequest"/> ready for human review.</returns>
-    public override ValueTask<ApprovalRequest> HandleAsync(
+    public override async ValueTask<ApprovalRequest> HandleAsync(
         GovernanceApprovalInput message,
         IWorkflowContext context,
         CancellationToken cancellationToken = default)
@@ -40,6 +46,25 @@ public sealed class CreateApprovalRequestExecutor(
             Approvers: decision.Approvers ?? [],
             RequestedAt: DateTimeOffset.UtcNow);
 
-        return ValueTask.FromResult(request);
+        if (escalationService is not null)
+        {
+            var escalationRequest = new EscalationRequest
+            {
+                EscalationId = Guid.NewGuid(),
+                AgentId = message.AgentId,
+                ToolName = message.ToolName,
+                Arguments = new Dictionary<string, string>(),
+                Description = request.Description,
+                RiskLevel = RiskLevel.Medium,
+                Priority = EscalationPriority.Blocking,
+                Approvers = decision.Approvers ?? [],
+                RequestedAt = request.RequestedAt,
+                OriginatingDecision = decision
+            };
+
+            await escalationService.QueueEscalationAsync(escalationRequest, cancellationToken);
+        }
+
+        return request;
     }
 }
diff --git a/src/Content/Domain/Domain.Common/Config/AI/Permissions/AutonomyTierPolicyConfig.cs b/src/Content/Domain/Domain.Common/Config/AI/Permissions/AutonomyTierPolicyConfig.cs
index 478a0ab..52242d7 100644
--- a/src/Content/Domain/Domain.Common/Config/AI/Permissions/AutonomyTierPolicyConfig.cs
+++ b/src/Content/Domain/Domain.Common/Config/AI/Permissions/AutonomyTierPolicyConfig.cs
@@ -17,4 +17,11 @@ public class AutonomyTierPolicyConfig
     /// ("Allow", "Ask", "Deny"). Enables specific tools for otherwise restricted agents.
     /// </summary>
     public Dictionary<string, string>? ToolOverrides { get; set; }
+
+    /// <summary>
+    /// Controls agent behavior while an escalation is pending for this tier.
+    /// Valid values: "Block" (agent waits for outcome) or "QueueAndContinue" (agent proceeds).
+    /// Defaults to "Block" for safety.
+    /// </summary>
+    public string EscalationBehavior { get; set; } = "Block";
 }
diff --git a/src/Content/Domain/Domain.Common/Result.cs b/src/Content/Domain/Domain.Common/Result.cs
index 0453d47..7c2a8fe 100644
--- a/src/Content/Domain/Domain.Common/Result.cs
+++ b/src/Content/Domain/Domain.Common/Result.cs
@@ -84,6 +84,9 @@ public class Result
 
     /// <summary>Creates a governance-blocked failure result.</summary>
     public static Result GovernanceBlocked(string reason) => new(false, [reason], ResultFailureType.GovernanceBlocked);
+
+    /// <summary>Creates a pending-approval result with the escalation ID for correlation.</summary>
+    public static Result PendingApproval(string reason) => new(false, [reason], ResultFailureType.PendingApproval);
 }
 
 /// <summary>
@@ -128,6 +131,9 @@ public sealed class Result<T> : Result
     /// <summary>Creates a governance-blocked failure result.</summary>
     public new static Result<T> GovernanceBlocked(string reason) => new(false, errors: [reason], failureType: ResultFailureType.GovernanceBlocked);
 
+    /// <summary>Creates a pending-approval result with the escalation ID for correlation.</summary>
+    public new static Result<T> PendingApproval(string reason) => new(false, errors: [reason], failureType: ResultFailureType.PendingApproval);
+
     /// <summary>
     /// Implicit conversion from a non-null value to a successful result.
     /// Throws <see cref="ArgumentNullException"/> if value is null to prevent
@@ -164,5 +170,7 @@ public enum ResultFailureType
     /// <summary>Permission check requires user confirmation before proceeding.</summary>
     PermissionRequired,
     /// <summary>Action blocked by governance policy.</summary>
-    GovernanceBlocked
+    GovernanceBlocked,
+    /// <summary>Action requires human approval; escalation is pending.</summary>
+    PendingApproval
 }
diff --git a/src/Content/Infrastructure/Infrastructure.AI/Agents/CapabilityMatchSupervisor.cs b/src/Content/Infrastructure/Infrastructure.AI/Agents/CapabilityMatchSupervisor.cs
index 58d7bdc..ab270c2 100644
--- a/src/Content/Infrastructure/Infrastructure.AI/Agents/CapabilityMatchSupervisor.cs
+++ b/src/Content/Infrastructure/Infrastructure.AI/Agents/CapabilityMatchSupervisor.cs
@@ -3,9 +3,11 @@ using System.Diagnostics;
 using Application.AI.Common.Factories;
 using Application.AI.Common.Interfaces;
 using Application.AI.Common.Interfaces.Agents;
+using Application.AI.Common.Interfaces.Escalation;
 using Application.AI.Common.Interfaces.Governance;
 using Application.AI.Common.OpenTelemetry.Metrics;
 using Domain.AI.Agents;
+using Domain.AI.Escalation;
 using Domain.AI.Governance;
 using Domain.AI.Orchestration;
 using Domain.AI.Telemetry.Conventions;
@@ -36,6 +38,7 @@ public sealed class CapabilityMatchSupervisor : ISupervisor, IDisposable
     private readonly IGovernanceAuditService _auditService;
     private readonly AgentExecutionContextFactory _contextFactory;
     private readonly IAgentFactory _agentFactory;
+    private readonly IEscalationService? _escalationService;
     private readonly IOptionsMonitor<AppConfig> _options;
     private readonly ILogger<CapabilityMatchSupervisor> _logger;
     private readonly SemaphoreSlim _concurrencySemaphore;
@@ -54,6 +57,7 @@ public sealed class CapabilityMatchSupervisor : ISupervisor, IDisposable
     /// <param name="agentFactory">Factory for creating configured AI agent instances.</param>
     /// <param name="options">Application configuration for orchestration settings.</param>
     /// <param name="logger">Logger instance.</param>
+    /// <param name="escalationService">Optional escalation service for autonomy tier violations.</param>
     public CapabilityMatchSupervisor(
         [FromKeyedServices("capability-match")] ISupervisorStrategy strategy,
         IDelegationStore delegationStore,
@@ -64,7 +68,8 @@ public sealed class CapabilityMatchSupervisor : ISupervisor, IDisposable
         AgentExecutionContextFactory contextFactory,
         IAgentFactory agentFactory,
         IOptionsMonitor<AppConfig> options,
-        ILogger<CapabilityMatchSupervisor> logger)
+        ILogger<CapabilityMatchSupervisor> logger,
+        IEscalationService? escalationService = null)
     {
         _strategy = strategy;
         _delegationStore = delegationStore;
@@ -76,6 +81,7 @@ public sealed class CapabilityMatchSupervisor : ISupervisor, IDisposable
         _agentFactory = agentFactory;
         _options = options;
         _logger = logger;
+        _escalationService = escalationService;
 
         var maxConcurrent = options.CurrentValue.AI?.Orchestration?.Subagent?.MaxConcurrentDelegations ?? 5;
         _concurrencySemaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);
@@ -100,7 +106,23 @@ public sealed class CapabilityMatchSupervisor : ISupervisor, IDisposable
         var selection = _strategy.SelectAgent(context);
 
         if (selection is null)
+        {
+            // When minimumTier > Restricted and escalation is available, treat as autonomy violation
+            var escalationConfig = _options.CurrentValue.AI?.Governance?.Escalation;
+            if (minimumTier > AutonomyLevel.Restricted
+                && _escalationService is not null
+                && escalationConfig?.Enabled == true)
+            {
+                var escalationResult = await HandleAutonomyEscalationAsync(
+                    taskDescription, requiredCapabilities, minimumTier,
+                    currentDelegationDepth, toolOverrides, escalationConfig, ct);
+
+                if (escalationResult is not null)
+                    return escalationResult;
+            }
+
             return DelegationResult.Fail("No capable agent found for the requested task and tier requirements.");
+        }
 
         var delegationId = Guid.NewGuid();
         var stopwatch = Stopwatch.StartNew();
@@ -158,6 +180,63 @@ public sealed class CapabilityMatchSupervisor : ISupervisor, IDisposable
         _activeDelegations.Clear();
     }
 
+    private async Task<DelegationResult?> HandleAutonomyEscalationAsync(
+        string taskDescription,
+        IReadOnlyList<string> requiredCapabilities,
+        AutonomyLevel minimumTier,
+        int currentDelegationDepth,
+        IReadOnlyList<string>? toolOverrides,
+        Domain.Common.Config.AI.Governance.EscalationConfig escalationConfig,
+        CancellationToken ct)
+    {
+        var escalationRequest = new EscalationRequest
+        {
+            EscalationId = Guid.NewGuid(),
+            AgentId = SupervisorId,
+            ToolName = $"delegate:{string.Join(",", requiredCapabilities)}",
+            Arguments = new Dictionary<string, string>
+            {
+                ["taskDescription"] = taskDescription,
+                ["minimumTier"] = minimumTier.ToString()
+            },
+            Description = $"Delegation blocked by autonomy tier ({minimumTier}): {taskDescription}",
+            RiskLevel = RiskLevel.Medium,
+            Priority = EscalationPriority.Blocking,
+            ApprovalStrategy = Enum.TryParse<ApprovalStrategyType>(
+                escalationConfig.DefaultApprovalStrategy, true, out var strategy)
+                ? strategy : ApprovalStrategyType.AnyOf,
+            Approvers = [],
+            QuorumThreshold = 1,
+            TimeoutSeconds = escalationConfig.DefaultTimeoutSeconds,
+            TimeoutAction = Enum.TryParse<EscalationTimeoutAction>(
+                escalationConfig.DefaultTimeoutAction, true, out var timeoutAction)
+                ? timeoutAction : EscalationTimeoutAction.DenyAndEscalate,
+            RequestedAt = DateTimeOffset.UtcNow
+        };
+
+        _logger.LogInformation(
+            "Autonomy tier violation — escalating delegation for {TaskDescription} (minimumTier: {MinimumTier})",
+            taskDescription, minimumTier);
+
+        var outcome = await _escalationService!.RequestEscalationAsync(escalationRequest, ct);
+
+        if (!outcome.IsApproved)
+        {
+            _logger.LogWarning("Escalation {EscalationId} denied for delegation: {TaskDescription}",
+                outcome.EscalationId, taskDescription);
+            return null;
+        }
+
+        _logger.LogInformation("Escalation {EscalationId} approved — retrying delegation with Restricted tier",
+            outcome.EscalationId);
+
+        // Retry with Restricted tier (removes tier barrier). minimumTier=Restricted
+        // prevents re-escalation since the guard checks minimumTier > Restricted.
+        return await DelegateAsync(
+            taskDescription, requiredCapabilities, AutonomyLevel.Restricted,
+            currentDelegationDepth, toolOverrides, ct);
+    }
+
     private SupervisorDecisionContext BuildDecisionContext(
         string taskDescription,
         IReadOnlyList<string> requiredCapabilities,
diff --git a/src/Content/Tests/Application.AI.Common.Tests/MediatRBehaviors/GovernancePolicyBehaviorEscalationTests.cs b/src/Content/Tests/Application.AI.Common.Tests/MediatRBehaviors/GovernancePolicyBehaviorEscalationTests.cs
new file mode 100644
index 0000000..8b2b001
--- /dev/null
+++ b/src/Content/Tests/Application.AI.Common.Tests/MediatRBehaviors/GovernancePolicyBehaviorEscalationTests.cs
@@ -0,0 +1,230 @@
+using Application.AI.Common.Interfaces.Agent;
+using Application.AI.Common.Interfaces.Escalation;
+using Application.AI.Common.Interfaces.Governance;
+using Application.AI.Common.Interfaces.MediatR;
+using Application.AI.Common.MediatRBehaviors;
+using Domain.AI.Escalation;
+using Domain.AI.Governance;
+using Domain.Common;
+using Domain.Common.Config.AI;
+using Domain.Common.Config.AI.Governance;
+using Domain.Common.Config.AI.Permissions;
+using MediatR;
+using Microsoft.Extensions.Logging;
+using Microsoft.Extensions.Options;
+using Moq;
+using Xunit;
+
+namespace Application.AI.Common.Tests.MediatRBehaviors;
+
+/// <summary>
+/// Tests for the <see cref="GovernancePolicyBehavior{TRequest, TResponse}"/> escalation
+/// integration. Validates RequireApproval decisions are routed through <see cref="IEscalationService"/>
+/// with correct blocking/non-blocking behavior.
+/// </summary>
+public sealed class GovernancePolicyBehaviorEscalationTests
+{
+    private readonly Mock<IGovernancePolicyEngine> _policyEngine = new();
+    private readonly Mock<IGovernanceAuditService> _auditService = new();
+    private readonly Mock<IAgentExecutionContext> _executionContext = new();
+    private readonly Mock<IEscalationService> _escalationService = new();
+    private readonly Mock<ILogger<GovernancePolicyBehavior<TestToolRequest, Result<string>>>> _logger = new();
+    private readonly GovernanceConfig _config;
+    private readonly PermissionsConfig _permissionsConfig;
+    private bool _nextCalled;
+
+    public GovernancePolicyBehaviorEscalationTests()
+    {
+        _config = new GovernanceConfig
+        {
+            Enabled = true,
+            EnableAudit = true,
+            Escalation = new EscalationConfig
+            {
+                Enabled = true,
+                DefaultTimeoutSeconds = 60,
+                DefaultTimeoutAction = "DenyAndEscalate",
+                DefaultApprovalStrategy = "AnyOf"
+            }
+        };
+
+        _permissionsConfig = new PermissionsConfig
+        {
+            TierPolicies = new Dictionary<string, AutonomyTierPolicyConfig>
+            {
+                ["Supervised"] = new() { EscalationBehavior = "Block" },
+                ["Autonomous"] = new() { EscalationBehavior = "QueueAndContinue" }
+            }
+        };
+
+        _executionContext.Setup(x => x.AgentId).Returns("test-agent");
+    }
+
+    private GovernancePolicyBehavior<TestToolRequest, Result<string>> CreateBehavior(
+        GovernanceConfig? configOverride = null,
+        PermissionsConfig? permissionsOverride = null)
+    {
+        var cfg = configOverride ?? _config;
+        var perm = permissionsOverride ?? _permissionsConfig;
+        return new GovernancePolicyBehavior<TestToolRequest, Result<string>>(
+            _policyEngine.Object,
+            _auditService.Object,
+            _executionContext.Object,
+            Mock.Of<IOptionsMonitor<GovernanceConfig>>(m => m.CurrentValue == cfg),
+            Mock.Of<IOptionsMonitor<PermissionsConfig>>(m => m.CurrentValue == perm),
+            _logger.Object,
+            _escalationService.Object);
+    }
+
+    private Task<Result<string>> Next()
+    {
+        _nextCalled = true;
+        return Task.FromResult(Result<string>.Success("ok"));
+    }
+
+    private static GovernanceDecision RequireApprovalDecision(
+        string rule = "high-risk-tool",
+        string policy = "security-policy",
+        string reason = "Requires approval",
+        IReadOnlyList<string>? approvers = null) =>
+        new(false, GovernancePolicyAction.RequireApproval, reason, rule, policy,
+            Approvers: approvers ?? ["admin@test.com"]);
+
+    [Fact]
+    public async Task Handle_RequireApprovalBlocking_CallsEscalationService()
+    {
+        _policyEngine.Setup(x => x.HasPolicies).Returns(true);
+        _policyEngine.Setup(x => x.EvaluateToolCall("test-agent", "deploy", null))
+            .Returns(RequireApprovalDecision());
+
+        _escalationService
+            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(new EscalationOutcome
+            {
+                EscalationId = Guid.NewGuid(),
+                IsApproved = true,
+                Decisions = [],
+                ResolutionType = EscalationResolutionType.Approved,
+                ResolvedAt = DateTimeOffset.UtcNow
+            });
+
+        var behavior = CreateBehavior();
+        await behavior.Handle(new TestToolRequest("deploy"), Next, CancellationToken.None);
+
+        _escalationService.Verify(
+            x => x.RequestEscalationAsync(
+                It.Is<EscalationRequest>(r =>
+                    r.AgentId == "test-agent" &&
+                    r.ToolName == "deploy" &&
+                    r.Approvers.Contains("admin@test.com")),
+                It.IsAny<CancellationToken>()),
+            Times.Once);
+    }
+
+    [Fact]
+    public async Task Handle_RequireApprovalBlocking_Approved_ProceedsWithNext()
+    {
+        _policyEngine.Setup(x => x.HasPolicies).Returns(true);
+        _policyEngine.Setup(x => x.EvaluateToolCall("test-agent", "deploy", null))
+            .Returns(RequireApprovalDecision());
+
+        _escalationService
+            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(new EscalationOutcome
+            {
+                EscalationId = Guid.NewGuid(),
+                IsApproved = true,
+                Decisions = [],
+                ResolutionType = EscalationResolutionType.Approved,
+                ResolvedAt = DateTimeOffset.UtcNow
+            });
+
+        var behavior = CreateBehavior();
+        var result = await behavior.Handle(new TestToolRequest("deploy"), Next, CancellationToken.None);
+
+        Assert.True(_nextCalled);
+        Assert.True(result.IsSuccess);
+    }
+
+    [Fact]
+    public async Task Handle_RequireApprovalBlocking_Denied_ReturnsDeniedResult()
+    {
+        _policyEngine.Setup(x => x.HasPolicies).Returns(true);
+        _policyEngine.Setup(x => x.EvaluateToolCall("test-agent", "deploy", null))
+            .Returns(RequireApprovalDecision());
+
+        _escalationService
+            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(new EscalationOutcome
+            {
+                EscalationId = Guid.NewGuid(),
+                IsApproved = false,
+                Decisions = [],
+                ResolutionType = EscalationResolutionType.Denied,
+                ResolvedAt = DateTimeOffset.UtcNow
+            });
+
+        var behavior = CreateBehavior();
+        var result = await behavior.Handle(new TestToolRequest("deploy"), Next, CancellationToken.None);
+
+        Assert.False(_nextCalled);
+        Assert.False(result.IsSuccess);
+        Assert.Equal(ResultFailureType.GovernanceBlocked, result.FailureType);
+    }
+
+    [Fact]
+    public async Task Handle_RequireApprovalQueueAndContinue_ReturnsPendingResult()
+    {
+        _policyEngine.Setup(x => x.HasPolicies).Returns(true);
+        _policyEngine.Setup(x => x.EvaluateToolCall("test-agent", "deploy", null))
+            .Returns(RequireApprovalDecision());
+
+        var escalationId = Guid.NewGuid();
+        _escalationService
+            .Setup(x => x.QueueEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(escalationId);
+
+        // Set tier to Autonomous which uses QueueAndContinue
+        var permConfig = new PermissionsConfig
+        {
+            TierPolicies = new Dictionary<string, AutonomyTierPolicyConfig>
+            {
+                ["Supervised"] = new() { EscalationBehavior = "QueueAndContinue" }
+            }
+        };
+
+        var behavior = CreateBehavior(permissionsOverride: permConfig);
+        var result = await behavior.Handle(new TestToolRequest("deploy"), Next, CancellationToken.None);
+
+        Assert.False(_nextCalled);
+        Assert.False(result.IsSuccess);
+        Assert.Equal(ResultFailureType.PendingApproval, result.FailureType);
+        Assert.Contains(escalationId.ToString(), result.Errors[0]);
+    }
+
+    [Fact]
+    public async Task Handle_RequireApproval_EscalationDisabled_FallsThrough()
+    {
+        _policyEngine.Setup(x => x.HasPolicies).Returns(true);
+        _policyEngine.Setup(x => x.EvaluateToolCall("test-agent", "deploy", null))
+            .Returns(RequireApprovalDecision());
+
+        var disabledConfig = new GovernanceConfig
+        {
+            Enabled = true,
+            EnableAudit = true,
+            Escalation = new EscalationConfig { Enabled = false }
+        };
+
+        var behavior = CreateBehavior(configOverride: disabledConfig);
+        var result = await behavior.Handle(new TestToolRequest("deploy"), Next, CancellationToken.None);
+
+        Assert.False(_nextCalled);
+        Assert.Equal(ResultFailureType.GovernanceBlocked, result.FailureType);
+        _escalationService.Verify(
+            x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()),
+            Times.Never);
+    }
+
+    public sealed record TestToolRequest(string ToolName) : IToolRequest;
+}
diff --git a/src/Content/Tests/Application.AI.Common.Tests/MediatRBehaviors/GovernancePolicyBehaviorTests.cs b/src/Content/Tests/Application.AI.Common.Tests/MediatRBehaviors/GovernancePolicyBehaviorTests.cs
index 99ecca7..2a05741 100644
--- a/src/Content/Tests/Application.AI.Common.Tests/MediatRBehaviors/GovernancePolicyBehaviorTests.cs
+++ b/src/Content/Tests/Application.AI.Common.Tests/MediatRBehaviors/GovernancePolicyBehaviorTests.cs
@@ -5,6 +5,7 @@ using Application.AI.Common.MediatRBehaviors;
 using Domain.AI.Governance;
 using Domain.Common;
 using Domain.Common.Config.AI;
+using Domain.Common.Config.AI.Permissions;
 using MediatR;
 using Microsoft.Extensions.Logging;
 using Microsoft.Extensions.Options;
@@ -20,12 +21,14 @@ public sealed class GovernancePolicyBehaviorTests
     private readonly Mock<IAgentExecutionContext> _executionContext = new();
     private readonly Mock<ILogger<GovernancePolicyBehavior<TestToolRequest, Result<string>>>> _logger = new();
     private readonly GovernanceConfig _config = new() { Enabled = true, EnableAudit = true };
+    private readonly PermissionsConfig _permissionsConfig = new();
     private readonly GovernancePolicyBehavior<TestToolRequest, Result<string>> _behavior;
     private bool _nextCalled;
 
     public GovernancePolicyBehaviorTests()
     {
         var monitor = Mock.Of<IOptionsMonitor<GovernanceConfig>>(m => m.CurrentValue == _config);
+        var permMonitor = Mock.Of<IOptionsMonitor<PermissionsConfig>>(m => m.CurrentValue == _permissionsConfig);
         _executionContext.Setup(x => x.AgentId).Returns("test-agent");
 
         _behavior = new GovernancePolicyBehavior<TestToolRequest, Result<string>>(
@@ -33,6 +36,7 @@ public sealed class GovernancePolicyBehaviorTests
             _auditService.Object,
             _executionContext.Object,
             monitor,
+            permMonitor,
             _logger.Object);
     }
 
@@ -50,6 +54,7 @@ public sealed class GovernancePolicyBehaviorTests
             _auditService.Object,
             _executionContext.Object,
             Mock.Of<IOptionsMonitor<GovernanceConfig>>(m => m.CurrentValue == _config),
+            Mock.Of<IOptionsMonitor<PermissionsConfig>>(m => m.CurrentValue == _permissionsConfig),
             Mock.Of<ILogger<GovernancePolicyBehavior<NonToolRequest, Result<string>>>>());
 
         var result = await behavior.Handle(new NonToolRequest(), () => Task.FromResult(Result<string>.Success("ok")), CancellationToken.None);
@@ -64,6 +69,7 @@ public sealed class GovernancePolicyBehaviorTests
         var behavior = new GovernancePolicyBehavior<TestToolRequest, Result<string>>(
             _policyEngine.Object, _auditService.Object, _executionContext.Object,
             Mock.Of<IOptionsMonitor<GovernanceConfig>>(m => m.CurrentValue == disabledConfig),
+            Mock.Of<IOptionsMonitor<PermissionsConfig>>(m => m.CurrentValue == _permissionsConfig),
             _logger.Object);
 
         var result = await behavior.Handle(new TestToolRequest("test"), Next, CancellationToken.None);
diff --git a/src/Content/Tests/Application.Core.Tests/Workflows/Governance/CreateApprovalRequestExecutorTests.cs b/src/Content/Tests/Application.Core.Tests/Workflows/Governance/CreateApprovalRequestExecutorTests.cs
new file mode 100644
index 0000000..f528221
--- /dev/null
+++ b/src/Content/Tests/Application.Core.Tests/Workflows/Governance/CreateApprovalRequestExecutorTests.cs
@@ -0,0 +1,112 @@
+using Application.AI.Common.Interfaces.Escalation;
+using Application.AI.Common.Interfaces.Governance;
+using Application.Core.Workflows.Governance;
+using Domain.AI.Escalation;
+using Domain.AI.Governance;
+using Microsoft.Agents.AI.Workflows;
+using Moq;
+using Xunit;
+
+namespace Application.Core.Tests.Workflows.Governance;
+
+/// <summary>
+/// Tests for <see cref="CreateApprovalRequestExecutor"/> verifying that it delegates
+/// to <see cref="IEscalationService"/> for notification dispatch and timeout tracking
+/// while continuing to produce the workflow <see cref="ApprovalRequest"/>.
+/// </summary>
+public sealed class CreateApprovalRequestExecutorTests
+{
+    private readonly Mock<IGovernanceAuditService> _auditService = new();
+    private readonly Mock<IEscalationService> _escalationService = new();
+    private readonly Mock<IWorkflowContext> _workflowContext = new();
+
+    [Fact]
+    public async Task HandleAsync_DelegatesToEscalationService()
+    {
+        var escalationId = Guid.NewGuid();
+        _escalationService
+            .Setup(x => x.QueueEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(escalationId);
+
+        var decision = new GovernanceDecision(
+            false, GovernancePolicyAction.RequireApproval,
+            "Requires approval", "high-risk", "security",
+            Approvers: ["admin@test.com"]);
+
+        var input = new GovernanceApprovalInput(
+            ToolName: "deploy",
+            ToolArguments: "{}",
+            AgentId: "test-agent",
+            InitialDecision: decision);
+
+        var executor = new CreateApprovalRequestExecutor(
+            _auditService.Object,
+            _escalationService.Object);
+
+        var result = await executor.HandleAsync(input, _workflowContext.Object, CancellationToken.None);
+
+        Assert.Equal("deploy", result.ToolName);
+        Assert.Equal("test-agent", result.AgentId);
+
+        _escalationService.Verify(
+            x => x.QueueEscalationAsync(
+                It.Is<EscalationRequest>(r =>
+                    r.AgentId == "test-agent" &&
+                    r.ToolName == "deploy"),
+                It.IsAny<CancellationToken>()),
+            Times.Once);
+    }
+
+    [Fact]
+    public async Task HandleAsync_StillReturnsApprovalRequest_WhenEscalationQueued()
+    {
+        _escalationService
+            .Setup(x => x.QueueEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(Guid.NewGuid());
+
+        var decision = new GovernanceDecision(
+            false, GovernancePolicyAction.RequireApproval,
+            "Requires approval", "rule-1", "policy-1",
+            Approvers: ["approver1"]);
+
+        var input = new GovernanceApprovalInput(
+            ToolName: "execute",
+            ToolArguments: "{\"cmd\":\"deploy\"}",
+            AgentId: "agent-1",
+            InitialDecision: decision);
+
+        var executor = new CreateApprovalRequestExecutor(
+            _auditService.Object,
+            _escalationService.Object);
+
+        var result = await executor.HandleAsync(input, _workflowContext.Object, CancellationToken.None);
+
+        Assert.Equal("execute", result.ToolName);
+        Assert.Equal("agent-1", result.AgentId);
+        Assert.Contains("approver1", result.Approvers);
+    }
+
+    [Fact]
+    public async Task HandleAsync_NoEscalationService_StillReturnsApprovalRequest()
+    {
+        var decision = new GovernanceDecision(
+            false, GovernancePolicyAction.RequireApproval,
+            "Requires approval", "rule-1", "policy-1",
+            Approvers: ["admin"]);
+
+        var input = new GovernanceApprovalInput(
+            ToolName: "write_file",
+            ToolArguments: "{}",
+            AgentId: "agent-2",
+            InitialDecision: decision);
+
+        var executor = new CreateApprovalRequestExecutor(
+            _auditService.Object,
+            escalationService: null);
+
+        var result = await executor.HandleAsync(input, _workflowContext.Object, CancellationToken.None);
+
+        Assert.Equal("write_file", result.ToolName);
+        Assert.Equal("agent-2", result.AgentId);
+    }
+}
diff --git a/src/Content/Tests/Domain.Common.Tests/Enums/ResultFailureTypeTests.cs b/src/Content/Tests/Domain.Common.Tests/Enums/ResultFailureTypeTests.cs
index 896a845..dcb44cc 100644
--- a/src/Content/Tests/Domain.Common.Tests/Enums/ResultFailureTypeTests.cs
+++ b/src/Content/Tests/Domain.Common.Tests/Enums/ResultFailureTypeTests.cs
@@ -18,6 +18,7 @@ public class ResultFailureTypeTests
     [InlineData(ResultFailureType.NotFound, 6)]
     [InlineData(ResultFailureType.PermissionRequired, 7)]
     [InlineData(ResultFailureType.GovernanceBlocked, 8)]
+    [InlineData(ResultFailureType.PendingApproval, 9)]
     public void Value_HasExpectedInteger(ResultFailureType type, int expected)
     {
         ((int)type).Should().Be(expected);
@@ -29,7 +30,7 @@ public class ResultFailureTypeTests
         var values = Enum.GetValues<ResultFailureType>();
 
         values.Should().OnlyHaveUniqueItems();
-        values.Should().HaveCount(9);
+        values.Should().HaveCount(10);
     }
 
     [Theory]
diff --git a/src/Content/Tests/Infrastructure.AI.Tests/Agents/CapabilityMatchSupervisorEscalationTests.cs b/src/Content/Tests/Infrastructure.AI.Tests/Agents/CapabilityMatchSupervisorEscalationTests.cs
new file mode 100644
index 0000000..fd80deb
--- /dev/null
+++ b/src/Content/Tests/Infrastructure.AI.Tests/Agents/CapabilityMatchSupervisorEscalationTests.cs
@@ -0,0 +1,230 @@
+using Application.AI.Common.Factories;
+using Application.AI.Common.Interfaces;
+using Application.AI.Common.Interfaces.Agents;
+using Application.AI.Common.Interfaces.Escalation;
+using Application.AI.Common.Interfaces.Governance;
+using Domain.AI.Agents;
+using Domain.AI.Escalation;
+using Domain.AI.Governance;
+using Domain.AI.Orchestration;
+using Domain.Common.Config;
+using Domain.Common.Config.AI;
+using Domain.Common.Config.AI.Governance;
+using Domain.Common.Config.AI.Orchestration;
+using Infrastructure.AI.Agents;
+using Microsoft.Agents.AI;
+using Microsoft.Extensions.AI;
+using Microsoft.Extensions.Logging.Abstractions;
+using Microsoft.Extensions.Options;
+using Moq;
+using Xunit;
+
+namespace Infrastructure.AI.Tests.Agents;
+
+/// <summary>
+/// Tests for <see cref="CapabilityMatchSupervisor"/> escalation integration.
+/// Validates that autonomy tier violations trigger <see cref="IEscalationService"/>
+/// and that approval-granted retries succeed with lowered tier.
+/// </summary>
+public sealed class CapabilityMatchSupervisorEscalationTests : IDisposable
+{
+    private readonly Mock<ISupervisorStrategy> _strategyMock = new();
+    private readonly Mock<IDelegationStore> _storeMock = new();
+    private readonly Mock<ISubagentProfileRegistry> _profileRegistryMock = new();
+    private readonly Mock<ISubagentToolResolver> _toolResolverMock = new();
+    private readonly Mock<IAutonomyTierResolver> _tierResolverMock = new();
+    private readonly Mock<IGovernanceAuditService> _auditServiceMock = new();
+    private readonly Mock<IAgentFactory> _agentFactoryMock = new();
+    private readonly Mock<IEscalationService> _escalationServiceMock = new();
+    private readonly IOptionsMonitor<AppConfig> _options;
+    private readonly CapabilityMatchSupervisor _supervisor;
+
+    private readonly SubagentDefinition _defaultDefinition = new()
+    {
+        AgentType = SubagentType.Execute,
+        AutonomyLevel = AutonomyLevel.Supervised
+    };
+
+    public CapabilityMatchSupervisorEscalationTests()
+    {
+        var config = new AppConfig
+        {
+            AI = new AIConfig
+            {
+                Orchestration = new OrchestrationConfig
+                {
+                    Subagent = new SubagentConfig
+                    {
+                        MaxDelegationDepth = 3,
+                        DelegationTimeoutSeconds = 30,
+                        MaxConcurrentDelegations = 5
+                    }
+                },
+                Governance = new GovernanceConfig
+                {
+                    Escalation = new EscalationConfig
+                    {
+                        Enabled = true,
+                        DefaultTimeoutSeconds = 60,
+                        DefaultTimeoutAction = "DenyAndEscalate",
+                        DefaultApprovalStrategy = "AnyOf"
+                    }
+                }
+            }
+        };
+        _options = Mock.Of<IOptionsMonitor<AppConfig>>(o => o.CurrentValue == config);
+
+        SetupDefaults();
+
+        var contextFactory = new AgentExecutionContextFactory(
+            NullLogger<AgentExecutionContextFactory>.Instance,
+            _options,
+            Mock.Of<IServiceProvider>(),
+            NullLoggerFactory.Instance);
+
+        _supervisor = new CapabilityMatchSupervisor(
+            _strategyMock.Object,
+            _storeMock.Object,
+            _profileRegistryMock.Object,
+            _toolResolverMock.Object,
+            _tierResolverMock.Object,
+            _auditServiceMock.Object,
+            contextFactory,
+            _agentFactoryMock.Object,
+            _options,
+            NullLogger<CapabilityMatchSupervisor>.Instance,
+            _escalationServiceMock.Object);
+    }
+
+    public void Dispose() => _supervisor.Dispose();
+
+    private void SetupDefaults()
+    {
+        _profileRegistryMock
+            .Setup(r => r.GetAllProfiles())
+            .Returns(new Dictionary<SubagentType, SubagentDefinition>
+            {
+                [SubagentType.Execute] = _defaultDefinition
+            });
+
+        _profileRegistryMock
+            .Setup(r => r.GetProfile(SubagentType.Execute))
+            .Returns(_defaultDefinition);
+
+        _toolResolverMock
+            .Setup(r => r.ResolveToolsForSubagent(It.IsAny<SubagentDefinition>(), It.IsAny<IReadOnlyList<AITool>>()))
+            .Returns(new List<AITool> { AIFunctionFactory.Create(() => "stub", "tool_a") });
+
+        _tierResolverMock
+            .Setup(r => r.Resolve(It.IsAny<SubagentDefinition>()))
+            .Returns(AutonomyLevel.Supervised);
+    }
+
+    [Fact]
+    public async Task DelegateAsync_AutonomyExceeded_TriggersEscalation()
+    {
+        _strategyMock
+            .Setup(s => s.SelectAgent(It.IsAny<SupervisorDecisionContext>()))
+            .Returns((AgentSelection?)null);
+
+        _escalationServiceMock
+            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(new EscalationOutcome
+            {
+                EscalationId = Guid.NewGuid(),
+                IsApproved = false,
+                Decisions = [],
+                ResolutionType = EscalationResolutionType.Denied,
+                ResolvedAt = DateTimeOffset.UtcNow
+            });
+
+        // minimumTier = Autonomous (higher than agents available) triggers autonomy violation
+        var result = await _supervisor.DelegateAsync(
+            "deploy to production",
+            ["tool_a"],
+            AutonomyLevel.Autonomous,
+            ct: CancellationToken.None);
+
+        _escalationServiceMock.Verify(
+            x => x.RequestEscalationAsync(
+                It.Is<EscalationRequest>(r =>
+                    r.Description.Contains("deploy to production") &&
+                    r.AgentId == nameof(CapabilityMatchSupervisor)),
+                It.IsAny<CancellationToken>()),
+            Times.Once);
+
+        Assert.False(result.IsSuccess);
+    }
+
+    [Fact]
+    public async Task DelegateAsync_AutonomyExceeded_Approved_RetriesWithLoweredTier()
+    {
+        var callCount = 0;
+        _strategyMock
+            .Setup(s => s.SelectAgent(It.IsAny<SupervisorDecisionContext>()))
+            .Returns(() =>
+            {
+                callCount++;
+                // First call: tier too high, return null
+                // Second call (retry with Restricted): return selection
+                if (callCount == 1) return null;
+                return new AgentSelection
+                {
+                    SelectedAgent = new AgentCandidate
+                    {
+                        AgentId = "Execute",
+                        AgentType = SubagentType.Execute,
+                        AutonomyLevel = AutonomyLevel.Supervised,
+                        AvailableTools = ["tool_a"]
+                    },
+                    ConfidenceScore = 0.9,
+                    Reasoning = "Best match"
+                };
+            });
+
+        _escalationServiceMock
+            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(new EscalationOutcome
+            {
+                EscalationId = Guid.NewGuid(),
+                IsApproved = true,
+                Decisions = [],
+                ResolutionType = EscalationResolutionType.Approved,
+                ResolvedAt = DateTimeOffset.UtcNow
+            });
+
+        _agentFactoryMock
+            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(Mock.Of<AIAgent>());
+
+        var result = await _supervisor.DelegateAsync(
+            "deploy to production",
+            ["tool_a"],
+            AutonomyLevel.Autonomous,
+            ct: CancellationToken.None);
+
+        Assert.True(result.IsSuccess);
+        Assert.Equal(2, callCount);
+    }
+
+    [Fact]
+    public async Task DelegateAsync_MinimumTierRestricted_NoEscalation()
+    {
+        _strategyMock
+            .Setup(s => s.SelectAgent(It.IsAny<SupervisorDecisionContext>()))
+            .Returns((AgentSelection?)null);
+
+        // minimumTier = Restricted (lowest) -- no autonomy violation, just no match
+        var result = await _supervisor.DelegateAsync(
+            "unknown task",
+            ["tool_z"],
+            AutonomyLevel.Restricted,
+            ct: CancellationToken.None);
+
+        _escalationServiceMock.Verify(
+            x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()),
+            Times.Never);
+
+        Assert.False(result.IsSuccess);
+    }
+}
