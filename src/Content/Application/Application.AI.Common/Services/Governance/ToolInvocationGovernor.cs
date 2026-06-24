using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Permissions;
using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Changes;
using Domain.AI.Governance;
using Domain.AI.Permissions;
using Domain.AI.Sandbox;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Permissions;
using Domain.Common.Config.AI.Sandbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// Scoped governor that authorizes the agent's autonomous tool calls on the live tool path,
/// applying the same permission, graded-autonomy risk, capability, and policy logic the MediatR
/// behaviours define — which never executed for agent tool calls because nothing produces
/// <c>IToolRequest</c>. Records every decision into a per-turn <see cref="GovernanceTrace"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Opt-in.</strong> Off unless <c>GovernanceConfig.EnforceToolInvocation</c> is true. When off,
/// <see cref="AuthorizeAsync"/> is a pure pass-through (no evaluation, no recording) so default
/// deployments are unchanged. When on, the gate is <em>fail-closed</em>.
/// </para>
/// <para>
/// <strong>Approval handling (PR1a).</strong> A tool that resolves to "requires approval" is recorded
/// as <see cref="ToolDecisionOutcome.PendingApproval"/> and blocked (fail-closed). Live human
/// escalation routing mid-tool-call is intentionally deferred to a focused follow-up; this degrades
/// safely to deny rather than silently allowing.
/// </para>
/// </remarks>
public sealed class ToolInvocationGovernor : IToolInvocationGovernor
{
    private readonly IAgentExecutionContext _executionContext;
    private readonly IToolPermissionService _toolPermissionService;
    private readonly IToolRiskClassifier _toolRiskClassifier;
    private readonly IAutonomyDecisionEvaluator _autonomyEvaluator;
    private readonly IGovernancePolicyEngine _policyEngine;
    private readonly IGovernanceAuditService _auditService;
    private readonly IDenialTracker _denialTracker;
    private readonly ICapabilityEnforcer _capabilityEnforcer;
    private readonly IOptionsMonitor<GovernanceConfig> _governanceConfig;
    private readonly IOptionsMonitor<PermissionsConfig> _permissionsConfig;
    private readonly IOptionsMonitor<SandboxConfig> _sandboxConfig;
    private readonly ILogger<ToolInvocationGovernor> _logger;

    private readonly object _lock = new();
    private readonly List<ToolDecisionRecord> _decisions = [];

    public ToolInvocationGovernor(
        IAgentExecutionContext executionContext,
        IToolPermissionService toolPermissionService,
        IToolRiskClassifier toolRiskClassifier,
        IAutonomyDecisionEvaluator autonomyEvaluator,
        IGovernancePolicyEngine policyEngine,
        IGovernanceAuditService auditService,
        IDenialTracker denialTracker,
        ICapabilityEnforcer capabilityEnforcer,
        IOptionsMonitor<GovernanceConfig> governanceConfig,
        IOptionsMonitor<PermissionsConfig> permissionsConfig,
        IOptionsMonitor<SandboxConfig> sandboxConfig,
        ILogger<ToolInvocationGovernor> logger)
    {
        _executionContext = executionContext;
        _toolPermissionService = toolPermissionService;
        _toolRiskClassifier = toolRiskClassifier;
        _autonomyEvaluator = autonomyEvaluator;
        _policyEngine = policyEngine;
        _auditService = auditService;
        _denialTracker = denialTracker;
        _capabilityEnforcer = capabilityEnforcer;
        _governanceConfig = governanceConfig;
        _permissionsConfig = permissionsConfig;
        _sandboxConfig = sandboxConfig;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<ToolInvocationDecision> AuthorizeAsync(string toolName, CancellationToken cancellationToken)
    {
        // Opt-in: when enforcement is off the governor never engages — pure pass-through, no record,
        // no behaviour change for existing deployments.
        if (!_governanceConfig.CurrentValue.EnforceToolInvocation)
            return ToolInvocationDecision.Allow();

        var profile = _toolRiskClassifier.Classify(toolName);

        // No agent identity means this isn't a fully-scoped agent turn (e.g. an execution path that
        // did not initialize the context). Mirror ToolPermissionBehavior and pass through rather than
        // guess an identity — but RECORD it as ungoverned (Enforced=false) so the trace surfaces the
        // gap instead of it being invisible to an evaluator.
        var agentId = _executionContext.AgentId;
        if (string.IsNullOrEmpty(agentId))
        {
            _logger.LogWarning(
                "Tool governance: no AgentId in execution context for {ToolName} — allowed ungoverned and recorded", toolName);
            Record(new ToolDecisionRecord(toolName, ToolDecisionOutcome.Allowed,
                "no agent identity in execution context", profile.Radius,
                RequiredApproval: false, ApprovalGranted: false, Enforced: false));
            return ToolInvocationDecision.Allow();
        }

        var permission = await _toolPermissionService
            .ResolvePermissionAsync(agentId, toolName, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // Graded-autonomy risk gate: can only tighten an Allow, never loosen (mirrors ToolPermissionBehavior).
        permission = ApplyRiskGate(permission, toolName, profile);

        switch (permission.Behavior)
        {
            case PermissionBehaviorType.Deny:
                _denialTracker.RecordDenial(agentId, toolName);
                return Blocked(toolName, ToolDecisionOutcome.Denied, permission.Reason, profile.Radius,
                    requiredApproval: false, agentId);

            case PermissionBehaviorType.Ask:
                _denialTracker.RecordDenial(agentId, toolName);
                return Blocked(toolName, ToolDecisionOutcome.PendingApproval,
                    $"requires approval: {permission.Reason}", profile.Radius,
                    requiredApproval: true, agentId);

            case PermissionBehaviorType.Allow:
                return await AuthorizeAllowedAsync(agentId, toolName, profile, cancellationToken)
                    .ConfigureAwait(false);

            default:
                // Unknown behaviour: fail closed.
                return Blocked(toolName, ToolDecisionOutcome.Denied,
                    $"unrecognized permission behaviour '{permission.Behavior}'", profile.Radius,
                    requiredApproval: false, agentId);
        }
    }

    private async ValueTask<ToolInvocationDecision> AuthorizeAllowedAsync(
        string agentId, string toolName, ToolRiskProfile profile, CancellationToken cancellationToken)
    {
        // Capability enforcement: the rule layer allowed the tool, now confirm the granted sandbox
        // capabilities satisfy what the tool needs.
        var grantedCapabilities = ToolCapability.None;
        foreach (var name in _sandboxConfig.CurrentValue.DefaultGrantedCapabilities)
            if (Enum.TryParse<ToolCapability>(name, ignoreCase: true, out var cap))
                grantedCapabilities |= cap;

        var capResult = await _capabilityEnforcer
            .EnforceAsync(toolName, grantedCapabilities, ct: cancellationToken)
            .ConfigureAwait(false);

        if (!capResult.IsSuccess)
        {
            var reason = capResult.Errors.Count > 0 ? capResult.Errors[0] : "capability check failed";
            return Blocked(toolName, ToolDecisionOutcome.Denied, $"capability violation: {reason}",
                profile.Radius, requiredApproval: false, agentId);
        }

        // Declarative policy layer (YAML policies), only when configured.
        var governance = _governanceConfig.CurrentValue;
        if (governance.Enabled && _policyEngine.HasPolicies)
        {
            var decision = _policyEngine.EvaluateToolCall(agentId, toolName);

            // The outcome is audited once below — by Blocked() on a deny/approval, or by the final
            // Allowed audit on success — so the policy action is not logged separately here.
            if (!decision.IsAllowed)
            {
                if (decision.Action == GovernancePolicyAction.RequireApproval)
                {
                    _denialTracker.RecordDenial(agentId, toolName);
                    return Blocked(toolName, ToolDecisionOutcome.PendingApproval,
                        $"requires approval: {decision.Reason}", profile.Radius,
                        requiredApproval: true, agentId);
                }

                _denialTracker.RecordDenial(agentId, toolName);
                return Blocked(toolName, ToolDecisionOutcome.Denied, decision.Reason, profile.Radius,
                    requiredApproval: false, agentId);
            }
        }

        Record(new ToolDecisionRecord(toolName, ToolDecisionOutcome.Allowed, "allowed",
            profile.Radius, RequiredApproval: false, ApprovalGranted: false, Enforced: true));

        if (governance.EnableAudit)
            _auditService.Log(agentId, toolName, ToolDecisionOutcome.Allowed.ToString());

        return ToolInvocationDecision.Allow();
    }

    /// <summary>
    /// Records a blocking decision (audit + trace) and returns a deny carrying a model-facing message.
    /// </summary>
    private ToolInvocationDecision Blocked(
        string toolName, ToolDecisionOutcome outcome, string reason, BlastRadius radius,
        bool requiredApproval, string agentId)
    {
        Record(new ToolDecisionRecord(toolName, outcome, reason, radius,
            RequiredApproval: requiredApproval, ApprovalGranted: false, Enforced: true));

        if (_governanceConfig.CurrentValue.EnableAudit)
            _auditService.Log(agentId, toolName, outcome.ToString());

        _logger.LogWarning(
            "Tool governance blocked agent {AgentId} tool {ToolName}: {Outcome} — {Reason}",
            agentId, toolName, outcome, reason);

        // Model-facing message is deliberately generic: the detailed reason (rule ids, paths,
        // capability internals) stays in the structured log and the GovernanceTrace, never relayed
        // to the LLM — avoids leaking operator-authored policy detail into model-visible content.
        return ToolInvocationDecision.Deny($"Error: tool '{toolName}' is not permitted in the current context.");
    }

    /// <summary>
    /// Applies the graded-autonomy risk gate to an Allow decision (a faithful copy of
    /// <c>ToolPermissionBehavior.ApplyRiskGate</c>). Tightens Allow → Ask/Deny when the active tier
    /// will not auto-approve the tool's blast radius; never loosens.
    /// </summary>
    private PermissionDecision ApplyRiskGate(PermissionDecision decision, string toolName, ToolRiskProfile profile)
    {
        if (decision.Behavior != PermissionBehaviorType.Allow)
            return decision;

        var permissions = _permissionsConfig.CurrentValue;
        if (!permissions.GradedAutonomy.Enabled)
            return decision;

        if (!Enum.TryParse<AutonomyLevel>(permissions.DefaultAutonomyLevel, ignoreCase: true, out var tier))
        {
            _logger.LogWarning(
                "Graded autonomy enabled but DefaultAutonomyLevel '{Tier}' is invalid — skipping risk gate for {ToolName}",
                permissions.DefaultAutonomyLevel, toolName);
            return decision;
        }

        var result = _autonomyEvaluator.Evaluate(
            tier, profile.Radius, ChangeTargetKind.Unspecified, isStateChange: !profile.IsReadOnly, skillKey: null);

        return result.Decision switch
        {
            AutonomyDecision.AutoApprove => decision,
            AutonomyDecision.RequiresApproval => PermissionDecision.Ask(
                $"graded autonomy: tool '{toolName}' (blast radius {profile.Radius}) requires approval under tier {tier}. {result.Reason}"),
            AutonomyDecision.Forbidden => PermissionDecision.Deny(
                $"graded autonomy: tool '{toolName}' (blast radius {profile.Radius}) is forbidden under tier {tier}. {result.Reason}"),
            _ => decision
        };
    }

    private void Record(ToolDecisionRecord record)
    {
        lock (_lock)
            _decisions.Add(record);
    }

    /// <inheritdoc />
    public void Reset()
    {
        lock (_lock)
            _decisions.Clear();
    }

    /// <inheritdoc />
    public GovernanceTrace GetTrace()
    {
        var enforced = _governanceConfig.CurrentValue.EnforceToolInvocation;
        lock (_lock)
        {
            if (!enforced && _decisions.Count == 0)
                return GovernanceTrace.Empty;

            return new GovernanceTrace
            {
                EnforcementEnabled = enforced,
                ToolDecisions = _decisions.ToList()
            };
        }
    }
}
