using Application.AI.Common.Interfaces.Orchestration.Magentic;
using Application.AI.Common.Services.Governance;
using Microsoft.Extensions.Logging;
using Presentation.AgentHub.AgUi;

namespace Presentation.AgentHub.Magentic;

/// <summary>
/// AG-UI notification channel for Magentic workflow progress. Translates the workflow's plan, rounds and
/// outcome into AG-UI SSE events and writes them to the active run's event stream.
/// </summary>
/// <remarks>
/// If no AG-UI run is active (the workflow ran from the console, a batch, or a non-SSE endpoint) the
/// notifier silently skips emission, as <c>AgUiPlanProgressNotifier</c> does: progress also flows through
/// spans and logs. A failed write is logged and contained; only cancellation propagates, so a client that
/// disconnects ends the run it was watching rather than being swallowed.
/// </remarks>
public sealed class AgUiMagenticProgressNotifier : IMagenticProgressNotifier
{
    /// <summary>
    /// The longest plan or instruction text put on the wire, in characters. The producer already bounds what
    /// it reports; this keeps the frame a browser receives bounded whatever produced it.
    /// </summary>
    public const int MaxTextLength = 4_000;

    /// <summary>
    /// The longest plan put on the wire for a plan review. Larger than <see cref="MaxTextLength"/> because a
    /// reviewer approves what they were shown; a cut here is flagged on the event rather than hidden.
    /// </summary>
    public const int MaxReviewTextLength = 32_000;

    private readonly IAgUiEventWriterAccessor _writerAccessor;
    private readonly ILogger<AgUiMagenticProgressNotifier> _logger;

    /// <summary>Initializes a new <see cref="AgUiMagenticProgressNotifier"/>.</summary>
    public AgUiMagenticProgressNotifier(
        IAgUiEventWriterAccessor writerAccessor,
        ILogger<AgUiMagenticProgressNotifier> logger)
    {
        _writerAccessor = writerAccessor;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task NotifyWorkflowStartedAsync(
        Guid workflowId, string workflowName, IReadOnlyList<string> participants, CancellationToken ct)
        => TryWriteAsync(
            new MagenticWorkflowStartedEvent
            {
                WorkflowId = workflowId.ToString(),
                WorkflowName = workflowName,
                Participants = participants,
            },
            "workflow-started", workflowId, ct);

    /// <inheritdoc />
    public Task NotifyPlanAsync(Guid workflowId, int planVersion, string planText, CancellationToken ct)
        => TryWriteAsync(
            new MagenticPlanEvent
            {
                WorkflowId = workflowId.ToString(),
                PlanVersion = planVersion,
                PlanText = Cap(planText, MaxTextLength).Text,
            },
            "plan", workflowId, ct);

    /// <inheritdoc />
    public Task NotifyRoundAsync(Guid workflowId, MagenticRoundReport round, CancellationToken ct)
        => TryWriteAsync(
            new MagenticRoundEvent
            {
                WorkflowId = workflowId.ToString(),
                Round = round.Round,
                NextSpeaker = round.NextSpeaker,
                Instruction = round.Instruction is null ? null : Cap(round.Instruction, MaxTextLength).Text,
                RequestSatisfied = round.RequestSatisfied,
                InLoop = round.InLoop,
                Progressing = round.Progressing,
            },
            "round", workflowId, ct);

    /// <inheritdoc />
    public Task NotifyPlanReviewRequestedAsync(
        Guid workflowId, string planText, bool planTruncated, bool isStalled, CancellationToken ct)
    {
        var (text, cut) = Cap(planText, MaxReviewTextLength);
        return TryWriteAsync(
            new MagenticPlanReviewRequestedEvent
            {
                WorkflowId = workflowId.ToString(),
                PlanText = text,
                PlanTruncated = planTruncated || cut,
                IsStalled = isStalled,
            },
            "plan-review-requested", workflowId, ct);
    }

    /// <inheritdoc />
    public Task NotifyWorkflowCompletedAsync(
        Guid workflowId, string completionReason, int roundsExecuted, CancellationToken ct)
        => TryWriteAsync(
            new MagenticWorkflowCompletedEvent
            {
                WorkflowId = workflowId.ToString(),
                CompletionReason = completionReason,
                RoundsExecuted = roundsExecuted,
            },
            "workflow-completed", workflowId, ct);

    /// <inheritdoc />
    public Task NotifyWorkflowFailedAsync(Guid workflowId, string errorCode, CancellationToken ct)
        => TryWriteAsync(
            new MagenticWorkflowFailedEvent { WorkflowId = workflowId.ToString(), ErrorCode = errorCode },
            "workflow-failed", workflowId, ct);

    private async Task TryWriteAsync(AgUiEvent evt, string eventKind, Guid workflowId, CancellationToken ct)
    {
        var writer = _writerAccessor.Writer;
        if (writer is null)
        {
            _logger.LogDebug(
                "No AG-UI writer active; skipping {EventKind} event for Magentic workflow {WorkflowId}.",
                eventKind, workflowId);
            return;
        }

        try
        {
            await writer.WriteAsync(evt, ct);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            // Only the run's own cancellation propagates. A cancellation the sink raised itself (a write
            // timeout) is the sink failing, not the run ending, and must not take the workflow down.
            _logger.LogWarning(
                ex, "Failed to write {EventKind} event for Magentic workflow {WorkflowId}.", eventKind, workflowId);
        }
    }

    // BoundedText, not a raw slice: it keeps the cut off a surrogate pair and marks it.
    private static (string Text, bool Truncated) Cap(string text, int ceiling)
        => BoundedText.Cap(text, ceiling, "…");
}
