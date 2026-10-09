using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Orchestration.Magentic;
using Application.AI.Common.Interfaces.Telemetry;
using Domain.AI.Telemetry.Conventions;
using Domain.Common;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Orchestration.Magentic;

#pragma warning disable MAAIW001 // MAF Magentic surface is experimental; pinned to public types only.

/// <summary>
/// Production implementation of <see cref="IMagenticOrchestrator"/>. Builds the
/// MAF <see cref="MagenticWorkflowBuilder"/>, opens a streaming run, and hands
/// each emitted <see cref="WorkflowEvent"/> to a per-run
/// <see cref="MagenticEventSubscriber"/> that emits the OTel span tree and
/// bridges the HITL plan-review through <see cref="IMagenticPlanReviewBridge"/>.
/// </summary>
/// <remarks>
/// <para>
/// The orchestrator owns the run loop because MAF's <c>MagenticOrchestrator</c>
/// is <see langword="internal"/>; instrumentation cannot attach by inheritance.
/// The public event stream is the only stable observation point.
/// </para>
/// <para>
/// The returned <see cref="MagenticWorkflowResult"/> is a <see cref="Result{T}"/>;
/// terminal errors surface as <see cref="Result.Fail(string[])"/> with stable
/// <c>magentic.*</c> codes and the underlying exception logged via structured
/// logging.
/// </para>
/// </remarks>
public sealed class MagenticOrchestrator : IMagenticOrchestrator
{
    private readonly MagenticSpanEmitter _spanEmitter;
    private readonly IMagenticPlanReviewBridge _planReviewBridge;
    private readonly MagenticChangeProposalRouter _changeProposalRouter;
    private readonly IContentCapturePolicy _contentCapturePolicy;
    private readonly ICompositeResponseSanitizer _sanitizer;
    private readonly IContentRedactionFilter _contentRedactionFilter;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MagenticOrchestrator> _logger;

    /// <summary>Creates a new orchestrator.</summary>
    public MagenticOrchestrator(
        MagenticSpanEmitter spanEmitter,
        IMagenticPlanReviewBridge planReviewBridge,
        MagenticChangeProposalRouter changeProposalRouter,
        IContentCapturePolicy contentCapturePolicy,
        ICompositeResponseSanitizer sanitizer,
        IContentRedactionFilter contentRedactionFilter,
        ILoggerFactory loggerFactory)
    {
        _spanEmitter = spanEmitter;
        _planReviewBridge = planReviewBridge;
        _changeProposalRouter = changeProposalRouter;
        _contentCapturePolicy = contentCapturePolicy;
        _sanitizer = sanitizer;
        _contentRedactionFilter = contentRedactionFilter;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<MagenticOrchestrator>();
    }

    /// <inheritdoc />
    public async Task<Result<MagenticWorkflowResult>> RunAsync(
        MagenticWorkflowRequest request,
        CancellationToken ct)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        var workflowId = request.WorkflowId ?? Guid.NewGuid();
        var workflowName = request.Name ?? $"workflow-{workflowId:N}";

        using var subscriber = new MagenticEventSubscriber(
            _spanEmitter,
            _planReviewBridge,
            _changeProposalRouter,
            _contentCapturePolicy,
            _sanitizer,
            _contentRedactionFilter,
            _loggerFactory.CreateLogger<MagenticEventSubscriber>());

        subscriber.StartWorkflow(request, workflowName, workflowId);

        var builder = new MagenticWorkflowBuilder(request.Manager)
            .AddParticipants(request.Participants)
            .WithMaxStalls(request.MaxStalls)
            .RequirePlanSignoff(request.RequirePlanSignoff);

        if (request.MaxRounds.HasValue) builder.WithMaxRounds(request.MaxRounds);
        if (request.MaxResets.HasValue) builder.WithMaxResets(request.MaxResets);

        var workflow = builder.Build();

        string completionReason;
        try
        {
            await using var run = await InProcessExecution
                .OpenStreamingAsync(workflow, workflowId.ToString(), ct)
                .ConfigureAwait(false);

            // MAF's Magentic orchestrator does not start itself: it needs the input messages AND a
            // TurnToken, and OpenStreamingAsync sends neither (only RunStreamingAsync does). Without
            // both the run just waits for input that never comes.
            // TrySendMessageAsync takes no token, so honour an already-cancelled caller before it starts
            // the manager's model calls.
            ct.ThrowIfCancellationRequested();

            var taskMessages = new List<ChatMessage> { new(ChatRole.User, request.Task) };
            if (!await run.TrySendMessageAsync(taskMessages).ConfigureAwait(false)
                || !await run.TrySendMessageAsync(new TurnToken(emitEvents: true)).ConfigureAwait(false))
            {
                _logger.LogError(
                    "Magentic workflow {WorkflowId} refused its task or start signal",
                    workflowId);
                return Fail(subscriber, "magentic.start_rejected");
            }

            await foreach (var evt in run.WatchStreamAsync(ct).ConfigureAwait(false))
            {
                var response = await subscriber.ProcessEventAsync(evt, ct).ConfigureAwait(false);
                if (response is not null)
                {
                    await run.SendResponseAsync(response).ConfigureAwait(false);
                }
            }

            // MAF's stream reader swallows cancellation and just ends the stream, which would read as a
            // clean completion with no output. Surface it as the cancellation it is — unless the run had
            // already produced its answer, which a late cancellation must not discard.
            if (subscriber.FinalOutput is null) ct.ThrowIfCancellationRequested();

            completionReason = DeriveCompletionReason(subscriber, request);
        }
        catch (OperationCanceledException)
        {
            return Fail(subscriber, "magentic.cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Magentic workflow {WorkflowId} failed with unhandled exception",
                workflowId);
            return Fail(subscriber, "magentic.unhandled_exception");
        }

        if (completionReason == MagenticConventions.CompletionReasonSatisfied && subscriber.FinalOutput is null)
        {
            _logger.LogWarning(
                "Magentic workflow {WorkflowId} ended as satisfied without producing a final answer",
                workflowId);
        }

        subscriber.EndWorkflow(completionReason);

        var result = new MagenticWorkflowResult
        {
            WorkflowId = workflowId,
            WorkflowName = workflowName,
            RoundsExecuted = subscriber.RoundsExecuted,
            ResetsExecuted = subscriber.ResetsExecuted,
            PlanReviewsExecuted = subscriber.PlanReviewsExecuted,
            CompletionReason = completionReason,
            FinalOutput = subscriber.FinalOutput,
            ErrorMessage = subscriber.ErrorMessage
        };

        return completionReason == MagenticConventions.CompletionReasonError
            ? Result<MagenticWorkflowResult>.Fail(subscriber.ErrorMessage ?? "magentic.error")
            : Result<MagenticWorkflowResult>.Success(result);
    }

    // Every early exit closes the workflow span as an error and returns a stable code; one helper so
    // the exits cannot drift apart.
    private static Result<MagenticWorkflowResult> Fail(MagenticEventSubscriber subscriber, string code)
    {
        subscriber.EndWorkflow(MagenticConventions.CompletionReasonError);
        return Result<MagenticWorkflowResult>.Fail(code);
    }

    private static string DeriveCompletionReason(MagenticEventSubscriber subscriber, MagenticWorkflowRequest request)
    {
        if (!string.IsNullOrEmpty(subscriber.ErrorMessage))
            return MagenticConventions.CompletionReasonError;
        if (request.MaxRounds.HasValue && subscriber.RoundsExecuted >= request.MaxRounds.Value)
            return MagenticConventions.CompletionReasonRoundLimit;
        if (request.MaxResets.HasValue && subscriber.ResetsExecuted >= request.MaxResets.Value)
            return MagenticConventions.CompletionReasonResetLimit;
        return MagenticConventions.CompletionReasonSatisfied;
    }
}

#pragma warning restore MAAIW001
