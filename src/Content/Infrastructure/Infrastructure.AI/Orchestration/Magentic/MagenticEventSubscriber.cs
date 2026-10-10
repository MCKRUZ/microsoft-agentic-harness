using System.Diagnostics;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Orchestration.Magentic;
using Application.AI.Common.Interfaces.Telemetry;
using Application.AI.Common.Services.Governance;
using Domain.AI.Telemetry.Conventions;
using Domain.AI.Telemetry.Redaction;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Specialized.Magentic;
using Microsoft.Extensions.AI;
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
/// the workflow's <see cref="StreamingRun.WatchStreamAsync(CancellationToken)"/> and hands each
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
    private readonly IContentCapturePolicy _contentCapturePolicy;
    private readonly ICompositeResponseSanitizer _sanitizer;
    private readonly IContentRedactionFilter _contentRedactionFilter;
    private readonly IMagenticProgressNotifier _progress;
    private readonly TimeSpan _terminalReportTimeout;
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

    /// <summary>The manager's final answer: the last message of the terminal transcript output.</summary>
    public string? FinalOutput => _finalOutput;

    /// <summary>Terminal error message (set on <see cref="WorkflowErrorEvent"/>).</summary>
    public string? ErrorMessage => _errorMessage;

    /// <summary>
    /// The longest plan or instruction text reported to the progress notifier, in characters. A plan is a
    /// model-authored document of unbounded length; a live surface shows a summary, not the whole ledger.
    /// </summary>
    public const int MaxProgressTextLength = 4_000;

    /// <summary>
    /// The longest plan reported for a plan review, in characters. Larger than a progress line because a
    /// reviewer approves what they were shown; when even this cuts the plan the report says so.
    /// </summary>
    public const int MaxReviewTextLength = 32_000;

    /// <summary>The longest speaker name reported, in characters. The manager model writes it; it is a name.</summary>
    public const int MaxSpeakerLength = 200;

    private static readonly TimeSpan DefaultTerminalReportTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Creates a new subscriber.</summary>
    public MagenticEventSubscriber(
        MagenticSpanEmitter emitter,
        IMagenticPlanReviewBridge planReviewBridge,
        MagenticChangeProposalRouter changeProposalRouter,
        IContentCapturePolicy contentCapturePolicy,
        ICompositeResponseSanitizer sanitizer,
        IContentRedactionFilter contentRedactionFilter,
        IMagenticProgressNotifier progress,
        ILogger<MagenticEventSubscriber> logger,
        TimeSpan? terminalReportTimeout = null)
    {
        _emitter = emitter;
        _planReviewBridge = planReviewBridge;
        _changeProposalRouter = changeProposalRouter;
        _contentCapturePolicy = contentCapturePolicy;
        _sanitizer = sanitizer;
        _contentRedactionFilter = contentRedactionFilter;
        _progress = progress;
        _terminalReportTimeout = terminalReportTimeout ?? DefaultTerminalReportTimeout;
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
                await HandlePlanCreatedAsync(planCreated, ct).ConfigureAwait(false);
                return null;

            case MagenticReplannedEvent replanned:
                await HandleReplannedAsync(replanned, ct).ConfigureAwait(false);
                return null;

            case MagenticProgressLedgerUpdatedEvent progress:
                await HandleProgressUpdatedAsync(progress, ct).ConfigureAwait(false);
                return null;

            case RequestInfoEvent requestInfo:
                return await HandleRequestInfoAsync(requestInfo, ct).ConfigureAwait(false);

            case WorkflowOutputEvent output:
                RecordFinalOutput(output);
                return null;

            case WorkflowErrorEvent error:
                _errorMessage = error.Exception?.Message ?? "magentic.error";
                return null;

            default:
                return null;
        }
    }

    /// <summary>
    /// Captures the manager's terminal answer: the last assistant message with text in the
    /// <c>List&lt;ChatMessage&gt;</c> transcript MAF emits when the run completes (measured against the
    /// real framework in <c>MagenticOrchestratorRunTests</c>). Participants' replies also arrive as
    /// <see cref="WorkflowOutputEvent"/> subclasses (<see cref="AgentResponseUpdateEvent"/>,
    /// <see cref="AgentResponseEvent"/>) and may be tagged <see cref="OutputTag.Intermediate"/>; neither
    /// is the answer. An unrecognised payload is logged and left unset rather than stringified into a
    /// type name.
    /// </summary>
    private void RecordFinalOutput(WorkflowOutputEvent output)
    {
        if (output is AgentResponseUpdateEvent or AgentResponseEvent) return;
        if (output.Tags.Contains(OutputTag.Intermediate)) return;

        // ChatMessage.Text is "" (not null) for a message with only tool-call content, and the user's
        // own task is in the transcript too: take the last assistant message that has text.
        var text = output.Data switch
        {
            IEnumerable<ChatMessage> transcript => transcript
                .Where(m => m.Role == ChatRole.Assistant)
                .Select(m => m.Text)
                .LastOrDefault(t => !string.IsNullOrWhiteSpace(t)),
            _ => LogUnrecognisedPayload(output)
        };

        if (!string.IsNullOrWhiteSpace(text)) _finalOutput = text;
    }

    private string? LogUnrecognisedPayload(WorkflowOutputEvent output)
    {
        // The shape a MAF upgrade would produce; it must be visible rather than a silent null answer.
        _logger.LogWarning(
            "Magentic workflow={WorkflowId} emitted terminal output of unrecognised payload type {PayloadType}",
            _workflowId,
            output.Data?.GetType().FullName ?? "<null>");
        return null;
    }

    private async Task HandlePlanCreatedAsync(MagenticPlanCreatedEvent evt, CancellationToken ct)
    {
        _planVersion = 1;
        MagenticSpanEmitter.RecordPlanCreated(_managerSpan, _planVersion);
        _logger.LogDebug(
            "Magentic plan created: workflow={WorkflowId} version={PlanVersion}",
            _workflowId,
            _planVersion);

        await ReportAsync(
            "plan", () => _progress.NotifyPlanAsync(
                _workflowId, _planVersion, Treat(evt.FullTaskLedger?.Text) ?? string.Empty, ct), ct).ConfigureAwait(false);
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

        await ReportAsync(
            "plan", () => _progress.NotifyPlanAsync(
                _workflowId, _planVersion, Treat(replanText) ?? string.Empty, ct), ct).ConfigureAwait(false);

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

    private async Task HandleProgressUpdatedAsync(MagenticProgressLedgerUpdatedEvent evt, CancellationToken ct)
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

        var round = _roundCount;
        await ReportAsync(
            "round", () => _progress.NotifyRoundAsync(
                _workflowId,
                new MagenticRoundReport
                {
                    Round = round,
                    NextSpeaker = Treat(nextSpeaker, MaxSpeakerLength),
                    Instruction = Treat(ledger?.InstructionOrQuestion),
                    RequestSatisfied = requestSatisfied,
                    InLoop = inLoop,
                    Progressing = progressing,
                },
                ct), ct).ConfigureAwait(false);
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

        // Before the bridge is asked: the reviewer is a human who can only act on a plan they have been
        // shown, and the bridge call can block for minutes.
        // The text is treated inside the guarded call: a sanitizer or redaction fault while preparing a
        // progress line must not be able to break the plan-review path itself.
        await ReportAsync(
            "review", () =>
            {
                var (text, truncated) = TreatBounded(review.Plan?.Text, MaxReviewTextLength);
                return _progress.NotifyPlanReviewRequestedAsync(
                    _workflowId, text ?? string.Empty, truncated, review.IsStalled, ct);
            }, ct).ConfigureAwait(false);

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

    /// <summary>Reports that the workflow has started, to the progress notifier.</summary>
    public Task NotifyStartedAsync(CancellationToken ct)
    {
        var participants = _request?.Participants.Select(p => p.Id ?? p.Name ?? string.Empty).ToList() ?? [];
        return ReportAsync("started", () => _progress.NotifyWorkflowStartedAsync(
            _workflowId, _workflowName, participants, ct), ct);
    }

    /// <summary>Reports that the workflow ended with a result, to the progress notifier.</summary>
    public Task NotifyCompletedAsync(string completionReason, CancellationToken ct)
        => ReportTerminalAsync("completed", t => _progress.NotifyWorkflowCompletedAsync(
            _workflowId, completionReason, _roundCount, t), ct);

    /// <summary>Reports that the workflow ended without a usable result, to the progress notifier.</summary>
    public Task NotifyFailedAsync(string errorCode, CancellationToken ct)
        => ReportTerminalAsync("failed", t => _progress.NotifyWorkflowFailedAsync(_workflowId, errorCode, t), ct);

    // Sanitize, then redact, then bound: the same order every other trust-boundary exit uses (#470), so an
    // invisible character cannot split a secret past the redaction patterns. The plan and the manager's
    // instruction are model-authored, and a live surface is a place a secret must not appear.
    private string? Treat(string? text, int maxLength = MaxProgressTextLength)
        => TreatBounded(text, maxLength).Text;

    private (string? Text, bool Truncated) TreatBounded(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return (text, false);

        var treated = SanitizeThenRedact.Apply(text, _sanitizer, _contentRedactionFilter, RedactionCategories.All);
        return BoundedText.Cap(treated, maxLength, "…");
    }

    // Terminal reports go out when the run's own token may already be cancelled, so they cannot use it -
    // writing with a cancelled token throws before anything is sent and the surface would be left showing a
    // run in progress. They carry their own bound instead: a half-open client must not hold a finished or
    // cancelled run open forever.
    private async Task ReportTerminalAsync(string what, Func<CancellationToken, Task> notify, CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(_terminalReportTimeout);
        await ReportAsync(what, () => notify(bounded.Token), ct).ConfigureAwait(false);
    }

    // A progress sink is not the work it reports on: one that is down, or that times out and raises a
    // cancellation of its own, must not fail the workflow. Only the run's own cancellation propagates.
    private async Task ReportAsync(string what, Func<Task> notify, CancellationToken ct)
    {
        try
        {
            await notify().ConfigureAwait(false);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            _logger.LogWarning(
                ex, "Magentic workflow={WorkflowId} progress notification '{Notification}' failed",
                _workflowId, what);
        }
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
            _errorMessage,
            _sanitizer,
            _contentRedactionFilter);
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
