using Application.AI.Common.Interfaces.Planner;
using Domain.AI.Planner;
using Domain.AI.Sandbox;
using Microsoft.Extensions.Logging;
using Presentation.AgentHub.AgUi;

namespace Presentation.AgentHub.Planner;

/// <summary>
/// AG-UI notification channel for plan execution events. Translates domain plan
/// lifecycle events into AG-UI SSE events and writes them to the active run's event stream.
/// </summary>
/// <remarks>
/// If no AG-UI run is active (e.g., plan executed from ConsoleUI or a non-SSE context),
/// the notifier silently skips event emission. This is by design -- plan progress also
/// flows through other channels (logging, state store audit trail).
/// </remarks>
public sealed class AgUiPlanProgressNotifier : IPlanProgressNotifier
{
    private const int MaxOutputSummaryLength = 500;

    private readonly IAgUiEventWriterAccessor _writerAccessor;
    private readonly ILogger<AgUiPlanProgressNotifier> _logger;

    /// <summary>Initializes a new <see cref="AgUiPlanProgressNotifier"/>.</summary>
    public AgUiPlanProgressNotifier(
        IAgUiEventWriterAccessor writerAccessor,
        ILogger<AgUiPlanProgressNotifier> logger)
    {
        _writerAccessor = writerAccessor;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task NotifyPlanStartedAsync(PlanId planId, string planName, PlanGraph graph, CancellationToken ct)
    {
        var evt = new PlanStartedEvent
        {
            PlanId = planId.Value.ToString(),
            PlanName = planName,
            TotalSteps = graph.Steps.Count,
        };

        await TryWriteAsync(evt, "plan-started", planId, ct);
    }

    /// <inheritdoc />
    public async Task NotifyStepStartedAsync(PlanId planId, PlanStepId stepId, string stepName, StepType type, CancellationToken ct)
    {
        var evt = new PlanStepStartedEvent
        {
            PlanId = planId.Value.ToString(),
            StepId = stepId.Value.ToString(),
            StepName = stepName,
            StepType = type.ToString(),
        };

        await TryWriteAsync(evt, "step-started", planId, ct);
    }

    /// <inheritdoc />
    public async Task NotifyStepCompletedAsync(PlanId planId, PlanStepId stepId, StepExecutionStatus status, TimeSpan duration, string? outputSummary, CancellationToken ct)
    {
        var evt = new PlanStepCompletedEvent
        {
            PlanId = planId.Value.ToString(),
            StepId = stepId.Value.ToString(),
            Status = status.ToString(),
            DurationMs = (long)duration.TotalMilliseconds,
            OutputSummary = Truncate(outputSummary, MaxOutputSummaryLength),
        };

        await TryWriteAsync(evt, "step-completed", planId, ct);
    }

    /// <inheritdoc />
    public async Task NotifyStateUpdateAsync(PlanId planId, PlanStepId stepId, StepExecutionStatus previousStatus, StepExecutionStatus newStatus, CancellationToken ct)
    {
        var evt = new PlanStateUpdateEvent
        {
            PlanId = planId.Value.ToString(),
            Patch =
            [
                new JsonPatchOperation
                {
                    Op = "replace",
                    Path = $"/steps/{stepId.Value}/status",
                    Value = newStatus.ToString(),
                },
            ],
        };

        await TryWriteAsync(evt, "state-update", planId, ct);
    }

    /// <inheritdoc />
    public async Task NotifySandboxStatusAsync(PlanId planId, PlanStepId stepId, string toolName, SandboxIsolationLevel isolationLevel, ResourceUsage usage, string? attestationHash, CancellationToken ct)
    {
        var evt = new SandboxStatusEvent
        {
            PlanId = planId.Value.ToString(),
            StepId = stepId.Value.ToString(),
            ToolName = toolName,
            IsolationLevel = isolationLevel.ToString(),
            MemoryUsedBytes = usage.MemoryBytes,
            CpuTimeMs = (long)(usage.CpuTimeSeconds * 1000),
            AttestationHash = attestationHash,
        };

        await TryWriteAsync(evt, "sandbox-status", planId, ct);
    }

    /// <inheritdoc />
    public async Task NotifyPlanCompletedAsync(PlanId planId, TimeSpan totalDuration, CancellationToken ct)
    {
        var evt = new PlanCompletedEvent
        {
            PlanId = planId.Value.ToString(),
            TotalDurationMs = (long)totalDuration.TotalMilliseconds,
        };

        await TryWriteAsync(evt, "plan-completed", planId, ct);
    }

    /// <inheritdoc />
    public async Task NotifyPlanFailedAsync(PlanId planId, PlanStepId failedStepId, string errorMessage, CancellationToken ct)
    {
        var evt = new PlanFailedEvent
        {
            PlanId = planId.Value.ToString(),
            FailedStepId = failedStepId.Value.ToString(),
            ErrorMessage = Truncate(errorMessage, MaxOutputSummaryLength) ?? errorMessage,
        };

        await TryWriteAsync(evt, "plan-failed", planId, ct);
    }

    private async Task TryWriteAsync(AgUiEvent evt, string eventKind, PlanId planId, CancellationToken ct)
    {
        var writer = _writerAccessor.Writer;
        if (writer is null)
        {
            _logger.LogDebug("No AG-UI writer active; skipping {EventKind} event for plan {PlanId}.",
                eventKind, planId.Value);
            return;
        }

        try
        {
            await writer.WriteAsync(evt, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to write {EventKind} event for plan {PlanId}.",
                eventKind, planId.Value);
        }
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (value is null || value.Length <= maxLength)
            return value;

        return value[..maxLength];
    }
}
