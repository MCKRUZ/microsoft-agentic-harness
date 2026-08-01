using Application.AI.Common.CQRS.Bundles.DeleteBundle;
using Application.AI.Common.CQRS.Bundles.GetBundleRun;
using Application.AI.Common.CQRS.Bundles.RegisterBundle;
using Application.AI.Common.CQRS.Bundles.RunBundle;
using Application.AI.Common.Interfaces.Governance;
using Domain.AI.Bundles;
using Domain.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Presentation.ExecutionApi.DTOs;
using Presentation.ExecutionApi.Extensions;
using Presentation.Common.Extensions;
using Presentation.ExecutionApi.Services;
using Presentation.ExecutionApi.Streaming;

namespace Presentation.ExecutionApi.Controllers;

/// <summary>
/// REST surface for registering and running externally-authored agent bundles. Every endpoint dispatches via
/// MediatR so the pipeline behaviors (validation, audit) wrap each call, and the whole controller requires an
/// authenticated caller — the capability envelope resolved from that caller's identity is what confines a
/// run, so authentication is the load-bearing gate.
/// </summary>
/// <remarks>
/// The run endpoint resolves the caller's capability envelope <em>here</em>, at the transport boundary, and
/// passes it into the run command: a run therefore executes under the grant of the credential that
/// <em>invoked</em> it, never the one that registered the handle, so a leaked handle cannot escalate.
/// </remarks>
[ApiController]
[Route("api/bundles")]
[Authorize]
[EnableRateLimiting(ExecutionApiServiceCollectionExtensions.DefaultRateLimitPolicy)]
public sealed class BundlesController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ICapabilityEnvelopeResolver _envelopeResolver;

    /// <summary>Initializes the controller with its MediatR and envelope-resolver dependencies.</summary>
    public BundlesController(IMediator mediator, ICapabilityEnvelopeResolver envelopeResolver)
    {
        ArgumentNullException.ThrowIfNull(mediator);
        ArgumentNullException.ThrowIfNull(envelopeResolver);
        _mediator = mediator;
        _envelopeResolver = envelopeResolver;
    }

    /// <summary>Registers a bundle archive (multipart upload), returning a short-lived handle.</summary>
    [HttpPost]
    [EnableRateLimiting(ExecutionApiServiceCollectionExtensions.RegisterRateLimitPolicy)]
    [ProducesResponseType(typeof(RegisterBundleResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Register(IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
            return Problem(title: "Validation failed", detail: "A non-empty bundle archive file is required.",
                statusCode: StatusCodes.Status400BadRequest);

        var callerId = ResolveCallerId();
        if (callerId is null)
            return this.NoUsableIdentity();

        await using var stream = file.OpenReadStream();
        var result = await _mediator
            .Send(new RegisterBundleCommand { Archive = stream, OwnerId = callerId }, cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess || result.Value is null)
            return MapFailure(result);

        var response = new RegisterBundleResponse { Handle = result.Value.Handle, ExpiresAt = result.Value.ExpiresAt };
        return Created($"/api/bundles/{response.Handle}", response);
    }

    /// <summary>Starts an asynchronous run of a staged bundle, returning a job id to poll.</summary>
    [HttpPost("{handle}/runs")]
    [ProducesResponseType(typeof(StartRunResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Run(
        string handle, [FromBody] RunBundleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var callerId = ResolveCallerId();
        if (callerId is null)
            return this.NoUsableIdentity();

        // Resolve the per-caller grant at the transport boundary from the authenticated principal.
        var envelope = _envelopeResolver.Resolve(User);

        var result = await _mediator.Send(new RunBundleCommand
        {
            Handle = handle,
            UserMessages = request.UserMessages,
            MaxTurns = request.MaxTurns,
            Envelope = envelope,
            OwnerId = callerId,
            Stream = request.Stream
        }, cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess || result.Value is null)
            return MapFailure(result);

        var statusUrl = $"/api/bundles/{handle}/runs/{result.Value.JobId}";
        var streamUrl = request.Stream ? $"{statusUrl}/stream" : null;
        return Accepted(statusUrl, new StartRunResponse
        {
            JobId = result.Value.JobId, StatusUrl = statusUrl, StreamUrl = streamUrl
        });
    }

    /// <summary>
    /// Opens the live Server-Sent-Events feed for a run started with <c>stream: true</c>, driving it on this
    /// connection and streaming the agent's output as it is generated. Owner-locked exactly like the poll and
    /// delete endpoints: a caller can only stream a run they started.
    /// </summary>
    /// <remarks>
    /// Only a run reserved for streaming and still awaiting its stream is driveable here; a background run, a
    /// run already being streamed, or a finished run is a 409 — poll its status endpoint instead. The run
    /// executes under the capability envelope captured when it was started, and closing the connection cancels
    /// it (<see cref="HttpContext.RequestAborted"/> flows into the executor).
    /// </remarks>
    [HttpGet("{handle}/runs/{jobId}/stream")]
    [EnableRateLimiting(ExecutionApiServiceCollectionExtensions.StreamRateLimitPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Stream(
        string handle, string jobId, [FromServices] BundleRunStreamer streamer, CancellationToken cancellationToken)
    {
        var callerId = ResolveCallerId();
        if (callerId is null)
            return this.NoUsableIdentity();

        // Owner-scoped pre-flight: the poll query reports an unknown or foreign run as not found, so this both
        // resolves the record and enforces ownership before we commit the response to an event stream.
        var lookup = await _mediator
            .Send(new GetBundleRunQuery { Handle = handle, JobId = jobId, OwnerId = callerId }, cancellationToken)
            .ConfigureAwait(false);

        if (!lookup.IsSuccess || lookup.Value is null)
            return MapFailure(lookup);

        var record = lookup.Value;
        if (!record.Streaming || record.Status != BundleRunStatus.Queued)
        {
            return Problem(
                title: "Run not streamable",
                detail: "This run is not awaiting a stream. Poll its status endpoint for the result.",
                statusCode: StatusCodes.Status409Conflict);
        }

        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no"; // defeat proxy buffering so frames arrive promptly
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        using var writer = new BundleStreamEventWriter(Response.Body);
        await streamer.StreamAsync(record, writer, cancellationToken).ConfigureAwait(false);
        return new EmptyResult();
    }

    /// <summary>Reads the current status and (once complete) result of a run.</summary>
    [HttpGet("{handle}/runs/{jobId}")]
    [ProducesResponseType(typeof(BundleRunResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRun(string handle, string jobId, CancellationToken cancellationToken)
    {
        var callerId = ResolveCallerId();
        if (callerId is null)
            return this.NoUsableIdentity();

        var result = await _mediator
            .Send(new GetBundleRunQuery { Handle = handle, JobId = jobId, OwnerId = callerId }, cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess || result.Value is null)
            return MapFailure(result);

        return Ok(BundleRunResponse.FromRecord(result.Value));
    }

    /// <summary>Explicitly deletes a staged bundle (TTL also reclaims it). Idempotent.</summary>
    [HttpDelete("{handle}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Delete(string handle, CancellationToken cancellationToken)
    {
        var callerId = ResolveCallerId();
        if (callerId is null)
            return this.NoUsableIdentity();

        var result = await _mediator
            .Send(new DeleteBundleCommand { Handle = handle, OwnerId = callerId }, cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess ? NoContent() : MapFailure(result);
    }

    /// <summary>
    /// Resolves a stable identifier for the calling principal, used to bind and authorize bundle handles and
    /// runs per owner. Delegates wholly to <see cref="BundleCallerIdentity.StableId"/> so ownership accepts
    /// exactly the token shapes the knowledge scope does. In the anonymous development mode every request
    /// carries the same synthetic principal, so all callers share one owner — there is no cross-caller
    /// isolation without real authentication, by design.
    /// </summary>
    /// <remarks>
    /// There is deliberately no display-name fallback. <c>name</c> is not unique — two people can share one —
    /// so accepting it here would let unrelated callers collide into a single plan owner. It existed only to
    /// give the anonymous dev principal an identity; that principal now carries a synthetic <c>oid</c>
    /// instead, so the fallback bought nothing but a way for a real token with no stable claim to own records
    /// under a shared name.
    /// </remarks>
    private string? ResolveCallerId() => BundleCallerIdentity.StableId(User);

    private IActionResult MapFailure(Result result) =>
        this.FailureResponse(result, "Bundle operation failed");
}
