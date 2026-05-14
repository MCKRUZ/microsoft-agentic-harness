using System.Collections.Concurrent;
using System.Diagnostics;
using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agents;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Governance;
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
public sealed class CapabilityMatchSupervisor : ISupervisor, IDisposable
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
    /// <param name="options">Application configuration for orchestration settings.</param>
    /// <param name="logger">Logger instance.</param>
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
        IOptionsMonitor<AppConfig> options,
        ILogger<CapabilityMatchSupervisor> logger,
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
        _options = options;
        _logger = logger;
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

        var context = BuildDecisionContext(taskDescription, requiredCapabilities, minimumTier, currentDelegationDepth, maxDepth);
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
            $"selected (score: {selection.ConfidenceScore:F2}, reason: {selection.Reasoning})");

        return await ExecuteAndTrack(
            delegationId, pendingRecord, selection, toolOverrides, currentDelegationDepth,
            subagentConfig?.DelegationTimeoutSeconds ?? 300, stopwatch, ct);
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

    private async Task<DelegationResult?> HandleAutonomyEscalationAsync(
        string taskDescription,
        IReadOnlyList<string> requiredCapabilities,
        AutonomyLevel minimumTier,
        int currentDelegationDepth,
        IReadOnlyList<string>? toolOverrides,
        Domain.Common.Config.AI.Governance.EscalationConfig escalationConfig,
        CancellationToken ct)
    {
        var escalationRequest = new EscalationRequest
        {
            EscalationId = Guid.NewGuid(),
            AgentId = SupervisorId,
            ToolName = $"delegate:{string.Join(",", requiredCapabilities)}",
            Arguments = new Dictionary<string, string>
            {
                ["taskDescription"] = taskDescription,
                ["minimumTier"] = minimumTier.ToString()
            },
            Description = $"Delegation blocked by autonomy tier ({minimumTier}): {taskDescription}",
            RiskLevel = RiskLevel.Medium,
            Priority = EscalationPriority.Blocking,
            ApprovalStrategy = Enum.TryParse<ApprovalStrategyType>(
                escalationConfig.DefaultApprovalStrategy, true, out var strategy)
                ? strategy : ApprovalStrategyType.AnyOf,
            Approvers = [],
            QuorumThreshold = 1,
            TimeoutSeconds = escalationConfig.DefaultTimeoutSeconds,
            TimeoutAction = Enum.TryParse<EscalationTimeoutAction>(
                escalationConfig.DefaultTimeoutAction, true, out var timeoutAction)
                ? timeoutAction : EscalationTimeoutAction.DenyAndEscalate,
            RequestedAt = DateTimeOffset.UtcNow
        };

        if (_escalationService is not { } escalation)
            return null;

        _logger.LogInformation(
            "Autonomy tier violation — escalating delegation for {TaskDescription} (minimumTier: {MinimumTier})",
            taskDescription, minimumTier);

        try
        {
            var outcome = await escalation.RequestEscalationAsync(escalationRequest, ct);

            if (!outcome.IsApproved)
            {
                _logger.LogWarning("Escalation {EscalationId} denied for delegation: {TaskDescription}",
                    outcome.EscalationId, taskDescription);
                return null;
            }

            _logger.LogInformation("Escalation {EscalationId} approved — retrying delegation with Restricted tier",
                outcome.EscalationId);

            return await DelegateAsync(
                taskDescription, requiredCapabilities, AutonomyLevel.Restricted,
                currentDelegationDepth, toolOverrides, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Escalation service failed for delegation {TaskDescription} — denying (fail-closed)",
                taskDescription);
            return null;
        }
    }

    private SupervisorDecisionContext BuildDecisionContext(
        string taskDescription,
        IReadOnlyList<string> requiredCapabilities,
        AutonomyLevel minimumTier,
        int currentDepth,
        int maxDepth)
    {
        var profiles = _profileRegistry.GetAllProfiles();
        var candidates = new List<AgentCandidate>(profiles.Count);

        foreach (var (type, definition) in profiles)
        {
            var tools = _toolResolver.ResolveToolsForSubagent(definition, Array.Empty<AITool>());
            var toolNames = tools.Select(t => t.Name).ToList();
            var autonomy = _tierResolver.Resolve(definition);

            candidates.Add(new AgentCandidate
            {
                AgentId = type.ToString(),
                AgentType = type,
                AutonomyLevel = autonomy,
                AvailableTools = toolNames
            });
        }

        return new SupervisorDecisionContext
        {
            TaskDescription = taskDescription,
            RequiredCapabilities = requiredCapabilities,
            MinimumAutonomyLevel = minimumTier,
            AvailableAgents = candidates,
            CurrentDelegationDepth = currentDepth,
            MaxDelegationDepth = maxDepth
        };
    }

    private static DelegationRecord BuildPendingRecord(
        Guid delegationId,
        AgentSelection selection,
        string taskDescription,
        IReadOnlyList<string> requiredCapabilities,
        IReadOnlyList<string>? toolOverrides,
        int currentDepth)
    {
        return new DelegationRecord
        {
            DelegationId = delegationId,
            SupervisorId = SupervisorId,
            DelegateAgentId = selection.SelectedAgent.AgentId,
            DelegateAgentType = selection.SelectedAgent.AgentType,
            TaskDescription = taskDescription,
            RequiredCapabilities = requiredCapabilities,
            ToolOverrides = toolOverrides,
            AutonomyLevel = selection.SelectedAgent.AutonomyLevel,
            State = DelegationState.Pending,
            DelegationDepth = currentDepth,
            StartedAt = DateTimeOffset.UtcNow
        };
    }

    private async Task<DelegationResult> ExecuteAndTrack(
        Guid delegationId,
        DelegationRecord pendingRecord,
        AgentSelection selection,
        IReadOnlyList<string>? toolOverrides,
        int currentDepth,
        int timeoutSeconds,
        Stopwatch stopwatch,
        CancellationToken ct)
    {
        var acquired = await _concurrencySemaphore.WaitAsync(
            TimeSpan.FromSeconds(timeoutSeconds), ct);

        if (!acquired)
        {
            await RecordFailure(pendingRecord, "Concurrency semaphore acquisition timed out.", ct);
            return DelegationResult.Fail("Concurrency semaphore acquisition timed out.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        _activeDelegations[delegationId] = cts;

        try
        {
            return await ExecuteAgent(pendingRecord, selection, toolOverrides, currentDepth, stopwatch, cts.Token);
        }
        catch (OperationCanceledException)
        {
            var reason = ct.IsCancellationRequested ? "Delegation cancelled." : "Delegation timed out.";
            await RecordCancellation(pendingRecord, reason, ct);
            return DelegationResult.Fail(reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delegation {DelegationId} to {AgentId} failed",
                delegationId, selection.SelectedAgent.AgentId);
            await RecordFailure(pendingRecord, ex.Message, ct);
            return DelegationResult.Fail(ex.Message);
        }
        finally
        {
            _activeDelegations.TryRemove(delegationId, out _);
            _concurrencySemaphore.Release();
        }
    }

    private async Task<DelegationResult> ExecuteAgent(
        DelegationRecord pendingRecord,
        AgentSelection selection,
        IReadOnlyList<string>? toolOverrides,
        int currentDepth,
        Stopwatch stopwatch,
        CancellationToken ct)
    {
        var definition = _profileRegistry.GetProfile(selection.SelectedAgent.AgentType);
        var agentContext = _contextFactory.CreateFromDelegation(definition, toolOverrides, currentDepth + 1, pendingRecord.DelegationId);
        var agent = await _agentFactory.CreateAgentAsync(agentContext, ct);

        stopwatch.Stop();

        await RecordCompletion(pendingRecord, ct);

        var durationMs = stopwatch.ElapsedMilliseconds;

        SupervisorMetrics.DelegationsTotal.Add(1,
            new(SupervisorConventions.SupervisorId, SupervisorId),
            new(SupervisorConventions.DelegateAgentId, selection.SelectedAgent.AgentId),
            new(SupervisorConventions.Outcome, "completed"));

        SupervisorMetrics.DelegationDuration.Record(durationMs,
            new(SupervisorConventions.SupervisorId, SupervisorId),
            new(SupervisorConventions.DelegateAgentId, selection.SelectedAgent.AgentId));

        _auditService.Log(
            SupervisorId,
            $"completed:{selection.SelectedAgent.AgentId}",
            $"delegation {pendingRecord.DelegationId} completed in {durationMs}ms");

        _logger.LogInformation(
            "Delegation {DelegationId} to {AgentId} completed in {DurationMs}ms",
            pendingRecord.DelegationId, selection.SelectedAgent.AgentId, durationMs);

        return DelegationResult.Success(
            $"Agent {selection.SelectedAgent.AgentId} created for delegation {pendingRecord.DelegationId}",
            0,
            durationMs);
    }

    private async Task RecordCompletion(DelegationRecord pendingRecord, CancellationToken ct)
    {
        var record = pendingRecord with
        {
            State = DelegationState.Completed,
            CompletedAt = DateTimeOffset.UtcNow
        };

        await _delegationStore.AppendAsync(record, ct);
    }

    private async Task RecordFailure(DelegationRecord pendingRecord, string reason, CancellationToken ct)
    {
        _auditService.Log(SupervisorId, $"failed:{pendingRecord.DelegateAgentId}", reason);

        SupervisorMetrics.DelegationsTotal.Add(1,
            new(SupervisorConventions.SupervisorId, SupervisorId),
            new(SupervisorConventions.DelegateAgentId, pendingRecord.DelegateAgentId),
            new(SupervisorConventions.Outcome, "failed"));

        var record = pendingRecord with
        {
            State = DelegationState.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            FailureReason = reason
        };

        await _delegationStore.AppendAsync(record, ct);
    }

    private async Task RecordCancellation(DelegationRecord pendingRecord, string reason, CancellationToken ct)
    {
        _auditService.Log(SupervisorId, $"cancelled:{pendingRecord.DelegateAgentId}", reason);

        SupervisorMetrics.DelegationsTotal.Add(1,
            new(SupervisorConventions.SupervisorId, SupervisorId),
            new(SupervisorConventions.DelegateAgentId, pendingRecord.DelegateAgentId),
            new(SupervisorConventions.Outcome, "cancelled"));

        var record = pendingRecord with
        {
            State = DelegationState.Cancelled,
            CompletedAt = DateTimeOffset.UtcNow,
            FailureReason = reason
        };

        await _delegationStore.AppendAsync(record, ct);
    }
}
