using System.Diagnostics;
using Application.AI.Common.Interfaces.Orchestration.Magentic;
using Domain.AI.Telemetry.Conventions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Specialized.Magentic;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Orchestration.Magentic;

#pragma warning disable MAAIW001 // MAF Magentic surface is experimental; pinned to public event types only.

/// <summary>
/// Stateful per-workflow event consumer that observes the public MAF event
/// stream and emits the Magentic OTel span tree via
/// <see cref="MagenticSpanEmitter"/>. Drives derived round / reset / stall
/// counters off the event stream because MAF's
/// <c>MagenticTaskContext.TaskCounters</c> is internal.
/// </summary>
/// <remarks>
/// <para>
/// One instance per workflow run. <see cref="MagenticOrchestrator"/> creates the
/// subscriber, opens spans by calling <see cref="StartWorkflow"/>, then iterates
/// the workflow's <see cref="StreamingRun.WatchStreamAsync"/> and hands each
/// event to <see cref="ProcessEventAsync"/>. The HITL bridge round-trip is
/// driven through this class so the plan-review span lifetime spans the entire
/// pause.
/// </para>
/// <para>
/// Span counters mirror the schema doc (§5): a clean round decrements the
/// stall counter (floor 0); a round with <c>IsInLoop=true</c> OR
/// <c>IsProgressBeingMade=false</c> increments it. The doc also encodes that
/// the first <see cref="MagenticPlanCreatedEvent"/> equals plan version 1 and
/// each subsequent <see cref="MagenticReplannedEvent"/> increments it.
/// </para>
/// </remarks>
public sealed class MagenticEventSubscriber : IDisposable
{
    private readonly MagenticSpanEmitter _emitter;
    private readonly IMagenticPlanReviewBridge _planReviewBridge;
    private readonly MagenticChangeProposalRouter _changeProposalRouter;
    private readonly ILogger<MagenticEventSubscriber> _logger;

    private Activity? _workflowSpan;
    private Activity? _managerSpan;
    private Activity? _planReviewSpan;

    private MagenticWorkflowRequest? _request;
    private string _workflowName = string.Empty;
    private Guid _workflowId;
    private int _roundCount;
    private int _resetCount;
    private int _stallCounter;
    private int _planReviewCount;
    private int _planVersion;
    private bool _stalledOnLastPlanReview;
    private string? _finalOutput;
    private string? _errorMessage;

    /// <summary>Total coordination rounds executed (derived).</summary>
    public int RoundsExecuted => _roundCount;

    /// <summary>Total stall-triggered resets executed (derived).</summary>
    public int ResetsExecuted => _resetCount;

    /// <summary>Total HITL plan-review pauses observed.</summary>
    public int PlanReviewsExecuted => _planReviewCount;

    /// <summary>The manager's final output text (set on <see cref="WorkflowOutputEvent"/>).</summary>
    public string? FinalOutput => _finalOutput;

    /// <summary>Terminal error message (set on <see cref="WorkflowErrorEvent"/>).</summary>
    public string? ErrorMessage => _errorMessage;

    /// <summary>Creates a new subscriber.</summary>
    public MagenticEventSubscriber(
        MagenticSpanEmitter emitter,
        IMagenticPlanReviewBridge planReviewBridge,
        MagenticChangeProposalRouter changeProposalRouter,
        ILogger<MagenticEventSubscriber> logger)
    {
        _emitter = emitter;
        _planReviewBridge = planReviewBridge;
        _changeProposalRouter = changeProposalRouter;
        _logger = logger;
    }

    /// <summary>
    /// Opens the root workflow + manager spans and stamps the request's
    /// configuration attributes. Idempotent — second call is a no-op.
    /// </summary>
    public void StartWorkflow(MagenticWorkflowRequest request, string workflowName, Guid workflowId)
    {
        if (_workflowSpan is not null) return;
        _request = request;
        _workflowName = workflowName;
        _workflowId = workflowId;

        var participants = request.Participants.Select(p => p.Id ?? p.Name ?? string.Empty).ToList();
        _workflowSpan = _emitter.StartWorkflowSpan(
            workflowName,
            request.MaxRounds,
            request.MaxStalls,
            request.MaxResets,
            request.RequirePlanSignoff,
            participants);

        _managerSpan = _emitter.StartManagerSpan(_workflowSpan);
    }

    /// <summary>
    /// Process a single <see cref="WorkflowEvent"/>. Returns an optional
    /// <see cref="ExternalResponse"/> that the orchestrator MUST send back to
    /// the workflow via <c>StreamingRun.SendResponseAsync</c> (used for HITL
    /// plan-review replies); <see langword="null"/> otherwise.
    /// </summary>
    public async Task<ExternalResponse?> ProcessEventAsync(WorkflowEvent evt, CancellationToken ct)
    {
        switch (evt)
        {
            case MagenticPlanCreatedEvent planCreated:
                HandlePlanCreated(planCreated);
                return null;

            case MagenticReplannedEvent replanned:
                await HandleReplannedAsync(replanned, ct).ConfigureAwait(false);
                return null;

            case MagenticProgressLedgerUpdatedEvent progress:
                HandleProgressUpdated(progress);
                return null;

            case RequestInfoEvent requestInfo:
                return await HandleRequestInfoAsync(requestInfo, ct).ConfigureAwait(false);

            case WorkflowOutputEvent output:
                _finalOutput = output.Data?.ToString();
                return null;

            case WorkflowErrorEvent error:
                _errorMessage = error.Exception?.Message ?? "magentic.error";
                return null;

            default:
                return null;
        }
    }

    private void HandlePlanCreated(MagenticPlanCreatedEvent evt)
    {
        _planVersion = 1;
        MagenticSpanEmitter.RecordPlanCreated(_managerSpan, _planVersion);
        _logger.LogDebug(
            "Magentic plan created: workflow={WorkflowId} version={PlanVersion}",
            _workflowId,
            _planVersion);
    }

    private async Task HandleReplannedAsync(MagenticReplannedEvent evt, CancellationToken ct)
    {
        _planVersion++;
        var replanText = evt.FullTaskLedger?.Text ?? string.Empty;

        // Open + immediately close a reset span (we treat the replan event as the
        // single observable reset moment; the underlying close timing is internal).
        _resetCount++;
        var trigger = _stalledOnLastPlanReview
            ? MagenticConventions.ResetTriggerStall
            : MagenticConventions.ResetTriggerLedgerFailure;
        var resetSpan = _emitter.StartResetSpan(_managerSpan, _resetCount, trigger, _stalledOnLastPlanReview);
        resetSpan?.Dispose();

        MagenticSpanEmitter.RecordReplanned(_managerSpan, _planVersion);

        // Route the replan through the change-proposal pipeline when the new
        // ledger proposes a state-changing action.
        await _changeProposalRouter.TryRouteAsync(
            new MagenticReplanInfo
            {
                WorkflowId = _workflowId,
                WorkflowName = _workflowName,
                PlanVersion = _planVersion,
                ReplanText = replanText
            },
            ct).ConfigureAwait(false);

        // Reset the stall counter and the plan-review stall flag after replan.
        _stallCounter = 0;
        _stalledOnLastPlanReview = false;
    }

    private void HandleProgressUpdated(MagenticProgressLedgerUpdatedEvent evt)
    {
        _roundCount++;
        var ledger = evt.ProgressLedger;
        var inLoop = ledger?.IsInLoop ?? false;
        var progressing = ledger?.IsProgressBeingMade ?? true;
        var requestSatisfied = ledger?.IsRequestSatisfied ?? false;
        var nextSpeaker = ledger?.NextSpeaker;

        if (inLoop || !progressing)
        {
            _stallCounter++;
        }
        else if (_stallCounter > 0)
        {
            _stallCounter--;
        }

        var roundSpan = _emitter.StartRoundSpan(
            _managerSpan,
            _roundCount,
            _stallCounter,
            requestSatisfied,
            inLoop,
            progressing,
            nextSpeaker);
        // Per the schema doc rounds are short-lived; close immediately after
        // attribute stamping. Child chat / execute_tool spans inherit the
        // current Activity via the MAF/OTel instrumentation already on the
        // chat client.
        roundSpan?.Dispose();
    }

    private async Task<ExternalResponse?> HandleRequestInfoAsync(RequestInfoEvent evt, CancellationToken ct)
    {
        var review = ExtractPlanReviewRequest(evt);
        if (review is null)
        {
            // Not a Magentic plan-review request — leave untouched. The orchestrator
            // does not respond, so the workflow will halt awaiting input that
            // never arrives — log a warning.
            _logger.LogWarning(
                "Unhandled RequestInfoEvent in Magentic workflow={WorkflowId}: payload type {PayloadType}",
                _workflowId,
                evt.Request?.Data?.GetType().FullName ?? "<null>");
            return null;
        }

        _planReviewCount++;
        _stalledOnLastPlanReview = review.IsStalled;

        _planReviewSpan = _emitter.StartPlanReviewSpan(
            _managerSpan,
            review.IsStalled,
            review.CurrentProgress is not null);

        var input = new MagenticPlanReviewInput
        {
            WorkflowId = _workflowId,
            WorkflowName = _workflowName,
            PlanText = review.Plan?.Text ?? string.Empty,
            IsStalled = review.IsStalled,
            ProgressLedgerSummary = SummarizeProgressLedger(review.CurrentProgress),
            Approver = _request?.PlanReviewApprover,
            TimeoutSeconds = _request?.PlanReviewTimeoutSeconds
        };

        MagenticPlanReviewOutcome outcome;
        try
        {
            outcome = await _planReviewBridge.RequestPlanReviewAsync(input, ct).ConfigureAwait(false);
        }
        catch
        {
            // Bridge threw — close the span as "revised" so the outcome attribute
            // exists, then bubble the exception. The workflow halts with an error
            // event the orchestrator surfaces as a Result.Fail.
            MagenticSpanEmitter.EndPlanReviewSpan(_planReviewSpan, approved: false);
            _planReviewSpan = null;
            throw;
        }

        MagenticSpanEmitter.EndPlanReviewSpan(_planReviewSpan, outcome.Approved);
        _planReviewSpan = null;

        var response = outcome.Approved
            ? review.Approve()
            : review.Revise(outcome.RevisionFeedback ?? "Plan revised by reviewer.");

        return evt.Request!.CreateResponse(response);
    }

    private static MagenticPlanReviewRequest? ExtractPlanReviewRequest(RequestInfoEvent evt)
    {
        var data = evt.Request?.Data;
        if (data is null) return null;
        return data.Is<MagenticPlanReviewRequest>(out var review) ? review : null;
    }

    private static string? SummarizeProgressLedger(MagenticProgressLedger? ledger)
    {
        if (ledger is null) return null;
        return string.Concat(
            "satisfied=", ledger.IsRequestSatisfied,
            ", inLoop=", ledger.IsInLoop,
            ", progressing=", ledger.IsProgressBeingMade,
            ", nextSpeaker=", ledger.NextSpeaker ?? "<null>");
    }

    /// <summary>
    /// Closes the workflow span with the terminal completion reason and the
    /// derived counters. Idempotent.
    /// </summary>
    public void EndWorkflow(string completionReason)
    {
        _managerSpan?.Dispose();
        _managerSpan = null;
        MagenticSpanEmitter.EndWorkflowSpan(
            _workflowSpan,
            _roundCount,
            _resetCount,
            completionReason,
            _errorMessage);
        _workflowSpan = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _planReviewSpan?.Dispose();
        _managerSpan?.Dispose();
        _workflowSpan?.Dispose();
    }
}

#pragma warning restore MAAIW001
