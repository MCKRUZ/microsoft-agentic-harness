using System.Diagnostics;
using System.Text.Json;
using Application.AI.Common.Interfaces.Attestation;
using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Interfaces.Sandbox;
using Domain.AI.Governance;
using Domain.AI.Planner;
using Domain.AI.Sandbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Planner.StepExecutors;

/// <summary>
/// Executes tool steps by routing through the appropriate sandbox, verifying attestation,
/// and enforcing capability-based permissions with never-downgrade isolation.
/// </summary>
public sealed class ToolUseStepExecutor : IPlanStepExecutor
{
    private readonly ICapabilityEnforcer _capabilityEnforcer;
    private readonly IServiceProvider _serviceProvider;
    private readonly IAttestationService _attestationService;
    private readonly IPlanProgressNotifier _notifier;
    private readonly PlanExecutionContext _executionContext;
    private readonly ILogger<ToolUseStepExecutor> _logger;

    public ToolUseStepExecutor(
        ICapabilityEnforcer capabilityEnforcer,
        IServiceProvider serviceProvider,
        IAttestationService attestationService,
        IPlanProgressNotifier notifier,
        PlanExecutionContext executionContext,
        ILogger<ToolUseStepExecutor> logger)
    {
        _capabilityEnforcer = capabilityEnforcer;
        _serviceProvider = serviceProvider;
        _attestationService = attestationService;
        _notifier = notifier;
        _executionContext = executionContext;
        _logger = logger;
    }

    public async Task<StepExecutionResult> ExecuteAsync(
        PlanStep step,
        IReadOnlyDictionary<PlanStepId, string> upstreamOutputs,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        if (step.Configuration is not ToolUseConfig config)
        {
            return new StepExecutionResult
            {
                Status = StepExecutionStatus.Failed,
                Duration = TimeSpan.Zero,
                ErrorMessage = $"Step '{step.Name}' has invalid configuration type for ToolUse executor."
            };
        }

        await _notifier.NotifyStepStartedAsync(
            _executionContext.CurrentPlanId ?? new PlanId(Guid.Empty), step.Id, step.Name, StepType.ToolUse, ct);

        var profile = await _capabilityEnforcer.ResolveProfileAsync(config.ToolName, ct);
        var isolationLevel = DetermineIsolation(config, profile, step);

        var input = BuildToolInput(config, upstreamOutputs);
        var request = new SandboxExecutionRequest
        {
            ToolName = config.ToolName,
            Input = input,
            Limits = new ResourceLimits(),
            PermissionProfile = profile,
            Timeout = step.Timeout
        };

        var executor = _serviceProvider.GetRequiredKeyedService<ISandboxExecutor>(isolationLevel);
        SandboxExecutionResult sandboxResult;
        try
        {
            sandboxResult = await executor.ExecuteAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            _logger.LogError(ex, "Sandbox execution threw for tool {Tool} in step {Step}", config.ToolName, step.Name);
            return new StepExecutionResult
            {
                Status = StepExecutionStatus.Failed,
                ErrorMessage = $"Sandbox execution exception: {ex.Message}",
                Duration = sw.Elapsed
            };
        }

        if (sandboxResult.Attestation is not null)
        {
            var verified = await _attestationService.VerifyAsync(sandboxResult.Attestation, ct);
            if (!verified)
            {
                sw.Stop();
                _logger.LogWarning("Attestation verification failed for tool {Tool} in step {Step}",
                    config.ToolName, step.Name);
                return new StepExecutionResult
                {
                    Status = StepExecutionStatus.Failed,
                    ErrorMessage = "Attestation verification failed: possible tampering detected.",
                    Duration = sw.Elapsed,
                    Attestation = sandboxResult.Attestation
                };
            }
        }

        sw.Stop();

        await _notifier.NotifySandboxStatusAsync(
            _executionContext.CurrentPlanId ?? new PlanId(Guid.Empty), step.Id, config.ToolName, isolationLevel,
            sandboxResult.ResourceUsage ?? new ResourceUsage(),
            sandboxResult.Attestation?.Signature, ct);

        if (sandboxResult.Success)
        {
            return new StepExecutionResult
            {
                Status = StepExecutionStatus.Completed,
                Output = sandboxResult.Output,
                Duration = sw.Elapsed,
                Attestation = sandboxResult.Attestation
            };
        }

        return new StepExecutionResult
        {
            Status = StepExecutionStatus.Failed,
            ErrorMessage = sandboxResult.ErrorMessage ?? "Tool execution failed.",
            Duration = sw.Elapsed,
            Attestation = sandboxResult.Attestation
        };
    }

    private static SandboxIsolationLevel DetermineIsolation(
        ToolUseConfig config,
        ToolPermissionProfile profile,
        PlanStep step)
    {
        var level = profile.MinimumIsolation;

        if (config.IsolationLevelOverride.HasValue && config.IsolationLevelOverride.Value > level)
            level = config.IsolationLevelOverride.Value;

        if (step.RequiredAutonomyLevel is AutonomyLevel.Supervised or AutonomyLevel.Restricted)
        {
            if (level < SandboxIsolationLevel.Container)
                level = SandboxIsolationLevel.Container;
        }

        return level;
    }

    private static string BuildToolInput(
        ToolUseConfig config,
        IReadOnlyDictionary<PlanStepId, string> upstreamOutputs)
    {
        var merged = new Dictionary<string, object?>(config.InputParameters);

        foreach (var (_, output) in upstreamOutputs)
        {
            if (string.IsNullOrEmpty(output)) continue;
            try
            {
                using var doc = JsonDocument.Parse(output);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    merged.TryAdd(prop.Name, prop.Value.GetRawText());
                }
            }
            catch (JsonException) { }
        }

        return JsonSerializer.Serialize(merged);
    }
}
