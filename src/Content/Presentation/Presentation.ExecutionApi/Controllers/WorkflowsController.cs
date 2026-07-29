using Application.AI.Common.CQRS.Workflows.GetRun;
using Application.AI.Common.CQRS.Workflows.StartRun;
using Application.AI.Common.CQRS.Workflows.Submit;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Runs;
using Domain.Common;
using Presentation.ExecutionApi.DTOs;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Presentation.ExecutionApi.Extensions;
using Presentation.ExecutionApi.Services;
using Presentation.ExecutionApi.Streaming;
using Presentation.Common.Extensions;

namespace Presentation.ExecutionApi.Controllers;

/// <summary>
/// REST surface for submitting externally-authored workflows. A submission is admitted, mapped to an
/// executable plan, and stored under the caller's own scope; running it is a separate operation.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Submission and execution are deliberately separable.</strong> Submitting stores a workflow
/// and nothing more; running one spends the host's model and tool credentials. They are distinct
/// endpoints so a consumer <em>can</em> attach a different policy to each — as shipped both carry the
/// same class-level authorization, so the separation is structural rather than enforced. It still
/// holds that authoring confers nothing: a caller can only run workflows it owns, under its own
/// envelope.
/// </para>
/// <para>
/// <strong>Ownership is never taken from the request.</strong> There is no owner field on the wire and
/// none passed into the command. The knowledge scope established by <c>KnowledgeScopeMiddleware</c>
/// from the authenticated principal is what the plan store stamps.
/// </para>
/// <para>
/// There is deliberately <em>no</em> identity check in the action itself. In this codebase an unscoped
/// write is not a private record but a world-readable one, so the instinct is to re-check here — but
/// <c>KnowledgeScopeMiddleware</c> runs before authorization, resolves identity through the same single
/// authority (<c>ClaimsPrincipalExtensions.GetUserIdOrNull</c>), and already answers 401 when an
/// authenticated principal carries nothing usable. A duplicate check would be unreachable, and an
/// unreachable check is worse than none: it cannot be tested, so it reads as protection that is not
/// being verified. What actually guards this is the middleware being mounted, which
/// <c>KnowledgeScopePipelineTests</c> asserts against this very host.
/// </para>
/// </remarks>
[ApiController]
[Route("api/workflows")]
[Authorize]
[EnableRateLimiting(ExecutionApiServiceCollectionExtensions.DefaultRateLimitPolicy)]
public sealed class WorkflowsController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ICapabilityEnvelopeResolver _envelopeResolver;
    private readonly IRunProgressBroker _progressBroker;

    /// <summary>Initializes the controller with its MediatR and envelope-resolver dependencies.</summary>
    public WorkflowsController(
        IMediator mediator,
        ICapabilityEnvelopeResolver envelopeResolver,
        IRunProgressBroker progressBroker)
    {
        ArgumentNullException.ThrowIfNull(mediator);
        ArgumentNullException.ThrowIfNull(envelopeResolver);
        ArgumentNullException.ThrowIfNull(progressBroker);

        _mediator = mediator;
        _envelopeResolver = envelopeResolver;
        _progressBroker = progressBroker;
    }

    /// <summary>
    /// Submits a workflow definition, returning its server-minted identifier and the mapping from the
    /// caller's step names to the identifiers the harness assigned them.
    /// </summary>
    /// <param name="definition">The workflow to admit and store.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    [HttpPost]
    [ServiceFilter(typeof(WorkflowRequestSizeLimitFilter))]
    [ProducesResponseType(typeof(SubmitWorkflowResult), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Submit(
        [FromBody] WorkflowDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var result = await _mediator
            .Send(new SubmitWorkflowCommand { Definition = definition }, cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess || result.Value is null)
            return MapFailure(result);

        return Created($"/api/workflows/{result.Value.WorkflowId}", result.Value);
    }


    /// <summary>
    /// Starts a run of a stored workflow, returning a job identifier to poll.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Accepts and queues; it does not wait. The response says the work was taken, not that it
    /// finished, which is why it carries a status URL rather than a result.
    /// </para>
    /// <para>
    /// Answers 409 while the workflow already has a run in progress. A workflow's execution state is
    /// held against the workflow rather than the run, so a second concurrent run would not be a second
    /// independent execution — it would share one state machine with the first.
    /// </para>
    /// </remarks>
    /// <param name="workflowId">The stored workflow to run.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    [HttpPost("{workflowId:guid}/runs")]
    [ProducesResponseType(typeof(StartWorkflowRunResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> StartRun(Guid workflowId, CancellationToken cancellationToken)
    {
        // Resolved here, at the transport boundary, from the credential that invoked THIS request —
        // never from anything stored with the workflow. A run therefore executes under the grant of
        // whoever started it, so a workflow authored by a broadly-permitted caller confers nothing on
        // a narrowly-permitted one that runs it later.
        var envelope = _envelopeResolver.Resolve(User);

        var result = await _mediator.Send(
            new StartWorkflowRunCommand
            {
                WorkflowId = workflowId,
                OwnerId = User.GetUserId(),
                TenantId = User.GetTenantId(),
                Envelope = envelope
            },
            cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess || result.Value is null)
            return MapFailure(result);

        var statusUrl = $"/api/workflows/{workflowId}/runs/{result.Value.JobId}";
        return Accepted(statusUrl, new StartWorkflowRunResponse
        {
            JobId = result.Value.JobId,
            StatusUrl = statusUrl
        });
    }

    /// <summary>Reads the current state of a run the caller started.</summary>
    /// <param name="workflowId">The workflow the run belongs to.</param>
    /// <param name="jobId">The run to read.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    [HttpGet("{workflowId:guid}/runs/{jobId}")]
    [ProducesResponseType(typeof(WorkflowRunResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRun(
        Guid workflowId, string jobId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(
            new GetWorkflowRunQuery
            {
                WorkflowId = workflowId,
                JobId = jobId,
                OwnerId = User.GetUserId(),
                TenantId = User.GetTenantId()
            },
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess && result.Value is not null
            ? Ok(WorkflowRunResponse.FromRecord(result.Value))
            : MapFailure(result);
    }

    /// <summary>Streams a run's progress as Server-Sent-Events until it finishes.</summary>
    /// <remarks>
    /// <para>
    /// Authorized exactly like reading the run: another caller's run, a job that never existed, and a
    /// job read through the wrong workflow's route are one answer, so a stream cannot be used to
    /// discover work the caller was not given the identifier for.
    /// </para>
    /// <para>
    /// Answers 503 when the host already carries as many streams as it permits. Each open stream holds
    /// a connection and a buffer for as long as the client keeps it, and serving one that cannot keep
    /// up would report the run with gaps in it — so the honest answer is to refuse and let the caller
    /// poll instead.
    /// </para>
    /// </remarks>
    /// <param name="workflowId">The workflow the run belongs to.</param>
    /// <param name="jobId">The run to watch.</param>
    /// <param name="cancellationToken">Ends the stream when the client disconnects.</param>
    [HttpGet("{workflowId:guid}/runs/{jobId}/stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> StreamRun(
        Guid workflowId, string jobId, CancellationToken cancellationToken)
    {
        // Authorized before anything is subscribed or written, so an unauthorized caller never holds
        // one of the host's stream slots.
        var result = await _mediator.Send(
            new GetWorkflowRunQuery
            {
                WorkflowId = workflowId,
                JobId = jobId,
                OwnerId = User.GetUserId(),
                TenantId = User.GetTenantId()
            },
            cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess || result.Value is null)
            return MapFailure(result);

        using var subscription = _progressBroker.Subscribe(result.Value.JobId);
        if (subscription is null)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                "This host is already carrying as many progress streams as it permits. "
                + "Poll the run's status instead, or retry shortly.");
        }

        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";

        // Chunked responses must not be buffered by a proxy, or the client receives the whole stream
        // at once when it is already over — which defeats the point of streaming it.
        Response.Headers["X-Accel-Buffering"] = "no";

        var streamer = new WorkflowProgressStreamer(Response.Body);
        await streamer.StreamAsync(result.Value, subscription, cancellationToken).ConfigureAwait(false);

        return new EmptyResult();
    }

    private IActionResult MapFailure(Result result) =>
        this.FailureResponse(result, "Workflow operation failed");
}
