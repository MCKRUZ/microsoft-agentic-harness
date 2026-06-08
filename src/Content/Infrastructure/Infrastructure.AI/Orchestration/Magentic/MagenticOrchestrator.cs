using Application.AI.Common.Interfaces.Orchestration.Magentic;
using Domain.AI.Telemetry.Conventions;
using Domain.Common;
using Microsoft.Agents.AI.Workflows;
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
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MagenticOrchestrator> _logger;

    /// <summary>Creates a new orchestrator.</summary>
    public MagenticOrchestrator(
        MagenticSpanEmitter spanEmitter,
        IMagenticPlanReviewBridge planReviewBridge,
        MagenticChangeProposalRouter changeProposalRouter,
        ILoggerFactory loggerFactory)
    {
        _spanEmitter = spanEmitter;
        _planReviewBridge = planReviewBridge;
        _changeProposalRouter = changeProposalRouter;
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

            await foreach (var evt in run.WatchStreamAsync(ct).ConfigureAwait(false))
            {
                var response = await subscriber.ProcessEventAsync(evt, ct).ConfigureAwait(false);
                if (response is not null)
                {
                    await run.SendResponseAsync(response).ConfigureAwait(false);
                }
            }

            completionReason = DeriveCompletionReason(subscriber, request);
        }
        catch (OperationCanceledException)
        {
            completionReason = MagenticConventions.CompletionReasonError;
            subscriber.EndWorkflow(completionReason);
            return Result<MagenticWorkflowResult>.Fail("magentic.cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Magentic workflow {WorkflowId} failed with unhandled exception",
                workflowId);
            completionReason = MagenticConventions.CompletionReasonError;
            subscriber.EndWorkflow(completionReason);
            return Result<MagenticWorkflowResult>.Fail("magentic.unhandled_exception");
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
