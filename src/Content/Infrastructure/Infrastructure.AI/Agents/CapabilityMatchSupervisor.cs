using System.Collections.Concurrent;
using System.Diagnostics;
using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agents;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Routing;
using Application.AI.Common.Interfaces.Skills;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Agents;
using Domain.AI.Escalation;
using Domain.AI.Governance;
using Domain.AI.Orchestration;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Agents;

/// <summary>
/// Deterministic capability-matching supervisor that delegates tasks to subagents
/// based on tool overlap, autonomy tier alignment, and agent-type fit scoring.
/// Uses <see cref="ISupervisorStrategy"/> (keyed <c>"capability-match"</c>) for
/// pluggable agent selection and <see cref="IDelegationStore"/> for append-only
/// delegation lifecycle tracking.
/// </summary>
public sealed partial class CapabilityMatchSupervisor : ISupervisor, IDisposable
{
    private const string SupervisorId = nameof(CapabilityMatchSupervisor);

    private readonly ISupervisorStrategy _strategy;
    private readonly IDelegationStore _delegationStore;
    private readonly ISubagentProfileRegistry _profileRegistry;
    private readonly ISubagentToolResolver _toolResolver;
    private readonly IAutonomyTierResolver _tierResolver;
    private readonly IGovernanceAuditService _auditService;
    private readonly AgentExecutionContextFactory _contextFactory;
    private readonly IAgentFactory _agentFactory;
    private readonly IAgentMetadataRegistry _agentRegistry;
    private readonly ISkillCompletionTracker _completionTracker;
    private readonly IModelRouter? _modelRouter;
    private readonly IEscalationService? _escalationService;
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly ILogger<CapabilityMatchSupervisor> _logger;
    private readonly SemaphoreSlim _concurrencySemaphore;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeDelegations = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="CapabilityMatchSupervisor"/> class.
    /// </summary>
    /// <param name="strategy">Keyed strategy (<c>"capability-match"</c>) for agent selection.</param>
    /// <param name="delegationStore">Append-only JSONL store for delegation lifecycle records.</param>
    /// <param name="profileRegistry">Registry of built-in subagent profiles.</param>
    /// <param name="toolResolver">Resolves effective tool pools per subagent definition.</param>
    /// <param name="tierResolver">Resolves effective autonomy tiers for agents.</param>
    /// <param name="auditService">Governance audit chain for delegation decisions.</param>
    /// <param name="contextFactory">Factory for building agent execution contexts.</param>
    /// <param name="agentFactory">Factory for creating configured AI agent instances.</param>
    /// <param name="agentRegistry">
    /// Discovers <c>AGENT.md</c>-registered peer agents for <see cref="DelegateToNamedAgentAsync"/>
    /// (#518) — resolving and validating the target the caller named.
    /// </param>
    /// <param name="completionTracker">
    /// Clears the per-delegation skill-prerequisite unlock scope after a named delegation finishes
    /// (#518 security-review finding) — see <see cref="ExecuteAgent"/>'s remarks for why the scope is
    /// the delegation id, not the target agent's id, and must be explicitly cleared here since a
    /// one-shot delegation has no cache eviction to hang the clear off.
    /// </param>
    /// <param name="options">Application configuration for orchestration settings.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="modelRouter">Optional model router for complexity-aware agent selection.</param>
    /// <param name="escalationService">Optional escalation service for autonomy tier violations.</param>
    public CapabilityMatchSupervisor(
        [FromKeyedServices("capability-match")] ISupervisorStrategy strategy,
        IDelegationStore delegationStore,
        ISubagentProfileRegistry profileRegistry,
        ISubagentToolResolver toolResolver,
        IAutonomyTierResolver tierResolver,
        IGovernanceAuditService auditService,
        AgentExecutionContextFactory contextFactory,
        IAgentFactory agentFactory,
        IAgentMetadataRegistry agentRegistry,
        ISkillCompletionTracker completionTracker,
        IOptionsMonitor<AppConfig> options,
        ILogger<CapabilityMatchSupervisor> logger,
        IModelRouter? modelRouter = null,
        IEscalationService? escalationService = null)
    {
        _strategy = strategy;
        _delegationStore = delegationStore;
        _profileRegistry = profileRegistry;
        _toolResolver = toolResolver;
        _tierResolver = tierResolver;
        _auditService = auditService;
        _contextFactory = contextFactory;
        _agentFactory = agentFactory;
        _agentRegistry = agentRegistry;
        _completionTracker = completionTracker;
        _options = options;
        _logger = logger;
        _modelRouter = modelRouter;
        _escalationService = escalationService;

        var maxConcurrent = options.CurrentValue.AI?.Orchestration?.Subagent?.MaxConcurrentDelegations ?? 5;
        _concurrencySemaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    /// <inheritdoc />
    public async Task<DelegationResult> DelegateAsync(
        string taskDescription,
        IReadOnlyList<string> requiredCapabilities,
        AutonomyLevel minimumTier,
        int currentDelegationDepth = 0,
        IReadOnlyList<string>? toolOverrides = null,
        CancellationToken ct = default)
    {
        var subagentConfig = _options.CurrentValue.AI?.Orchestration?.Subagent;
        var maxDepth = subagentConfig?.MaxDelegationDepth ?? 3;

        if (currentDelegationDepth >= maxDepth)
            return DelegationResult.Fail($"Delegation depth limit ({maxDepth}) exceeded.");

        var context = await BuildDecisionContextAsync(taskDescription, requiredCapabilities, minimumTier, currentDelegationDepth, maxDepth, ct);
        var selection = _strategy.SelectAgent(context);

        if (selection is null)
        {
            // When minimumTier > Restricted and escalation is available, treat as autonomy violation
            var escalationConfig = _options.CurrentValue.AI?.Governance?.Escalation;
            if (minimumTier > AutonomyLevel.Restricted
                && _escalationService is not null
                && escalationConfig?.Enabled == true)
            {
                var escalationResult = await HandleAutonomyEscalationAsync(
                    taskDescription, requiredCapabilities, minimumTier,
                    currentDelegationDepth, toolOverrides, escalationConfig, ct);

                if (escalationResult is not null)
                    return escalationResult;
            }

            return DelegationResult.Fail("No capable agent found for the requested task and tier requirements.");
        }

        return await RecordAndExecuteAsync(
            selection, taskDescription, requiredCapabilities, toolOverrides, currentDelegationDepth,
            subagentConfig?.DelegationTimeoutSeconds ?? 300,
            $"selected (score: {selection.ConfidenceScore:F2}, reason: {selection.Reasoning})",
            ct);
    }

    /// <inheritdoc />
    public async Task<DelegationResult> DelegateToNamedAgentAsync(
        string targetAgentId,
        string taskDescription,
        string? callingAgentId,
        int currentDelegationDepth = 0,
        IReadOnlyList<string>? toolOverrides = null,
        CancellationToken ct = default)
    {
        var subagentConfig = _options.CurrentValue.AI?.Orchestration?.Subagent;
        var maxDepth = subagentConfig?.MaxDelegationDepth ?? 3;

        if (currentDelegationDepth >= maxDepth)
            return DelegationResult.Fail($"Delegation depth limit ({maxDepth}) exceeded.");

        if (string.Equals(targetAgentId, callingAgentId, StringComparison.OrdinalIgnoreCase))
            return DelegationResult.Fail("Cannot delegate to self.");

        var target = _agentRegistry.TryGet(targetAgentId);
        if (target is null)
            return DelegationResult.Fail($"No registered agent with id '{targetAgentId}'.");

        // No autonomy tier to resolve from an AgentDefinition (it carries none) and no floor to
        // enforce against one — see DelegateToNamedAgentAsync's own remarks on ISupervisor for why.
        // Recorded, not enforced: DelegateToSubagentTool's own DefaultMinimumTier for the same reason
        // — a caller-facing record needs a value, and this is the same default every other call on
        // this tool falls back to when it does not name one.
        var selection = new AgentSelection
        {
            SelectedAgent = new AgentCandidate
            {
                AgentId = target.Id,
                AgentType = SubagentType.NamedAgent,
                AutonomyLevel = AutonomyLevel.Supervised,
                AvailableTools = []
            },
            ConfidenceScore = 1.0,
            Reasoning = "explicit target agent named by the caller"
        };

        return await RecordAndExecuteAsync(
            selection, taskDescription, [], toolOverrides, currentDelegationDepth,
            subagentConfig?.DelegationTimeoutSeconds ?? 300,
            $"named target (reason: {selection.Reasoning})",
            ct);
    }

    /// <summary>
    /// Records the pending delegation and runs it to completion — the shared tail of
    /// <see cref="DelegateAsync"/> and <see cref="DelegateToNamedAgentAsync"/> once each has built
    /// its own <see cref="AgentSelection"/> by whatever means fits (strategy-scored vs. named-target
    /// lookup). Only the audit message differs between callers, since the two selection paths mean
    /// different things by "why this agent."
    /// </summary>
    private async Task<DelegationResult> RecordAndExecuteAsync(
        AgentSelection selection,
        string taskDescription,
        IReadOnlyList<string> requiredCapabilities,
        IReadOnlyList<string>? toolOverrides,
        int currentDelegationDepth,
        int delegationTimeoutSeconds,
        string auditMessage,
        CancellationToken ct)
    {
        var delegationId = Guid.NewGuid();
        var stopwatch = Stopwatch.StartNew();

        var pendingRecord = BuildPendingRecord(
            delegationId, selection, taskDescription, requiredCapabilities,
            toolOverrides, currentDelegationDepth);

        await _delegationStore.AppendAsync(pendingRecord, ct);

        SupervisorMetrics.SelectionScore.Record(
            selection.ConfidenceScore,
            new(SupervisorConventions.SupervisorId, SupervisorId),
            new(SupervisorConventions.DelegateAgentId, selection.SelectedAgent.AgentId));

        _auditService.Log(
            SupervisorId,
            $"delegate:{selection.SelectedAgent.AgentId}",
            auditMessage);

        return await ExecuteAndTrack(
            delegationId, pendingRecord, selection, toolOverrides, currentDelegationDepth,
            delegationTimeoutSeconds, stopwatch, ct);
    }

    /// <inheritdoc />
    public Task<DelegationRecord?> GetDelegationStatusAsync(Guid delegationId, CancellationToken ct = default)
        => _delegationStore.GetByIdAsync(delegationId, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<DelegationRecord>> GetActiveDelegationsAsync(CancellationToken ct = default)
    {
        var all = await _delegationStore.GetBySessionAsync(SupervisorId, ct);
        return all
            .Where(r => r.State is DelegationState.Pending or DelegationState.InProgress)
            .ToList();
    }

    /// <inheritdoc />
    public Task<bool> CancelDelegationAsync(Guid delegationId, CancellationToken ct = default)
    {
        if (!_activeDelegations.TryGetValue(delegationId, out var cts))
            return Task.FromResult(false);

        cts.Cancel();
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _concurrencySemaphore.Dispose();
        foreach (var cts in _activeDelegations.Values)
            cts.Dispose();
        _activeDelegations.Clear();
    }
}
