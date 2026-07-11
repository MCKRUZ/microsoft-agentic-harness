using Application.AI.Common.CQRS.Bundles.DeleteBundle;
using Application.AI.Common.CQRS.Bundles.GetBundleRun;
using Application.AI.Common.CQRS.Bundles.RegisterBundle;
using Application.AI.Common.CQRS.Bundles.RunBundle;
using Application.AI.Common.Interfaces.Governance;
using Domain.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Presentation.BundleApi.DTOs;
using Presentation.BundleApi.Extensions;
using Presentation.BundleApi.Services;

namespace Presentation.BundleApi.Controllers;

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
[EnableRateLimiting(BundleApiServiceCollectionExtensions.DefaultRateLimitPolicy)]
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
    [EnableRateLimiting(BundleApiServiceCollectionExtensions.RegisterRateLimitPolicy)]
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
            return NoUsableIdentity();

        await using var stream = file.OpenReadStream();
        var result = await _mediator
            .Send(new RegisterBundleCommand { Archive = stream, OwnerId = callerId }, cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess || result.Value is null)
            return MapFailure(result.FailureType, result.Errors);

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
            return NoUsableIdentity();

        // Resolve the per-caller grant at the transport boundary from the authenticated principal.
        var envelope = _envelopeResolver.Resolve(User);

        var result = await _mediator.Send(new RunBundleCommand
        {
            Handle = handle,
            UserMessages = request.UserMessages,
            MaxTurns = request.MaxTurns,
            Envelope = envelope,
            OwnerId = callerId
        }, cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess || result.Value is null)
            return MapFailure(result.FailureType, result.Errors);

        var statusUrl = $"/api/bundles/{handle}/runs/{result.Value.JobId}";
        return Accepted(statusUrl, new StartRunResponse { JobId = result.Value.JobId, StatusUrl = statusUrl });
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
            return NoUsableIdentity();

        var result = await _mediator
            .Send(new GetBundleRunQuery { Handle = handle, JobId = jobId, OwnerId = callerId }, cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess || result.Value is null)
            return MapFailure(result.FailureType, result.Errors);

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
            return NoUsableIdentity();

        var result = await _mediator
            .Send(new DeleteBundleCommand { Handle = handle, OwnerId = callerId }, cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess ? NoContent() : MapFailure(result.FailureType, result.Errors);
    }

    /// <summary>
    /// Resolves a stable identifier for the calling principal, used to bind and authorize bundle handles and
    /// runs per owner. Prefers the Entra object id (<c>oid</c>, stable per user across tokens), then the
    /// subject (<c>sub</c>/name-identifier), then the name. In the anonymous development mode every request
    /// carries the same synthetic principal, so all callers share one owner — there is no cross-caller
    /// isolation without real authentication, by design.
    /// </summary>
    private string? ResolveCallerId() =>
        BundleCallerIdentity.StableId(User) ?? NullIfBlank(User.Identity?.Name);

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>401 response when the authenticated principal has no usable identity to own resources under.</summary>
    private IActionResult NoUsableIdentity() => Problem(
        title: "Unauthorized",
        detail: "The authenticated principal carries no usable identity.",
        statusCode: StatusCodes.Status401Unauthorized);

    /// <summary>
    /// Maps a failed <see cref="Result"/>/<see cref="Result{T}"/> to an HTTP problem response. Validation,
    /// NotFound, and auth reasons are safe to surface verbatim (validator strings / lookups / declared
    /// reasons); a general failure returns a generic message only — handlers have already logged the detail,
    /// and raw error text can leak internal state. Mirrors the mapping used by the harness's other controllers.
    /// </summary>
    private IActionResult MapFailure(ResultFailureType failureType, IReadOnlyList<string> errors) => failureType switch
    {
        ResultFailureType.NotFound => Problem(
            title: "Not Found", detail: string.Join(" / ", errors), statusCode: StatusCodes.Status404NotFound),
        ResultFailureType.Validation => Problem(
            title: "Validation failed", detail: string.Join(" / ", errors), statusCode: StatusCodes.Status400BadRequest),
        ResultFailureType.Unauthorized => Problem(
            title: "Unauthorized", detail: string.Join(" / ", errors), statusCode: StatusCodes.Status401Unauthorized),
        ResultFailureType.Forbidden => Problem(
            title: "Forbidden", detail: string.Join(" / ", errors), statusCode: StatusCodes.Status403Forbidden),
        _ => Problem(
            title: "Bundle operation failed",
            detail: "An error occurred processing the request. See server logs for details.",
            statusCode: StatusCodes.Status500InternalServerError),
    };
}
