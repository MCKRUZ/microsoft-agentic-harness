using Application.AI.Common.CQRS.Schedules;
using Application.AI.Common.Interfaces.Governance;
using Domain.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Presentation.Common.Extensions;
using Presentation.ExecutionApi.DTOs;
using Presentation.ExecutionApi.Extensions;

namespace Presentation.ExecutionApi.Controllers;

/// <summary>
/// REST surface for recurring schedules (#593). Creation resolves the caller's capability envelope
/// at the transport boundary, exactly like <see cref="WorkflowsController.StartRun"/> — the same
/// reason schedule creation cannot be an agent tool operation; see
/// <c>ManageSchedulesTool</c>'s remarks.
/// </summary>
/// <remarks>
/// Ownership is never taken from the request body. <c>KnowledgeScopeMiddleware</c> establishes the
/// authenticated principal before this controller runs; see <c>WorkflowsController</c>'s own remarks
/// for why no duplicate identity check belongs in the action.
/// </remarks>
[ApiController]
[Route("api/schedules")]
[Authorize]
[EnableRateLimiting(ExecutionApiServiceCollectionExtensions.DefaultRateLimitPolicy)]
public sealed class SchedulesController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ICapabilityEnvelopeResolver _envelopeResolver;

    public SchedulesController(IMediator mediator, ICapabilityEnvelopeResolver envelopeResolver)
    {
        ArgumentNullException.ThrowIfNull(mediator);
        ArgumentNullException.ThrowIfNull(envelopeResolver);

        _mediator = mediator;
        _envelopeResolver = envelopeResolver;
    }

    /// <summary>Creates a recurring schedule, returning its initial state.</summary>
    /// <param name="request">The schedule to create.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    [HttpPost]
    [ProducesResponseType(typeof(ScheduleResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Create([FromBody] CreateScheduleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Resolved here, at the transport boundary, from the credential that invoked THIS request —
        // never re-resolved on a later tick. See CreateScheduleCommand's own remarks for why this
        // cannot happen anywhere else.
        var envelope = _envelopeResolver.Resolve(User);

        var result = await _mediator.Send(
            new CreateScheduleCommand
            {
                Kind = request.Kind,
                TargetId = request.TargetId,
                OwnerId = User.GetUserId(),
                TenantId = User.GetTenantId(),
                Envelope = envelope,
                CronExpression = request.CronExpression,
                TimeZoneId = request.TimeZoneId,
                ActiveHoursStart = request.ActiveHoursStart,
                ActiveHoursEnd = request.ActiveHoursEnd,
                Cooldown = request.Cooldown,
                MissedRunPolicy = request.MissedRunPolicy,
            },
            cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess || result.Value is null)
            return MapFailure(result);

        var response = ScheduleResponse.FromSummary(result.Value);
        return Created($"/api/schedules/{response.ScheduleId}", response);
    }

    /// <summary>Lists every schedule the caller owns.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ScheduleResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(
            new ListSchedulesQuery { OwnerId = User.GetUserId(), TenantId = User.GetTenantId() },
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess && result.Value is not null
            ? Ok(result.Value.Select(ScheduleResponse.FromSummary).ToList())
            : MapFailure(result);
    }

    /// <summary>Pauses a schedule the caller owns.</summary>
    /// <param name="scheduleId">The schedule to pause.</param>
    /// <param name="request">The version the caller last read.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    [HttpPost("{scheduleId}/pause")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Pause(
        string scheduleId, [FromBody] ScheduleVersionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await _mediator.Send(
            new PauseScheduleCommand
            {
                ScheduleId = scheduleId,
                OwnerId = User.GetUserId(),
                TenantId = User.GetTenantId(),
                ExpectedVersion = request.ExpectedVersion,
            },
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? NoContent() : MapFailure(result);
    }

    /// <summary>Resumes a paused schedule the caller owns.</summary>
    /// <param name="scheduleId">The schedule to resume.</param>
    /// <param name="request">The version the caller last read.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    [HttpPost("{scheduleId}/resume")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Resume(
        string scheduleId, [FromBody] ScheduleVersionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await _mediator.Send(
            new ResumeScheduleCommand
            {
                ScheduleId = scheduleId,
                OwnerId = User.GetUserId(),
                TenantId = User.GetTenantId(),
                ExpectedVersion = request.ExpectedVersion,
            },
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? NoContent() : MapFailure(result);
    }

    /// <summary>Deletes a schedule the caller owns.</summary>
    /// <param name="scheduleId">The schedule to delete.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    [HttpDelete("{scheduleId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(string scheduleId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(
            new DeleteScheduleCommand { ScheduleId = scheduleId, OwnerId = User.GetUserId(), TenantId = User.GetTenantId() },
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? NoContent() : MapFailure(result);
    }

    private IActionResult MapFailure(Result result) => this.FailureResponse(result, "Schedule operation failed");

    private IActionResult MapFailure<T>(Result<T> result) => this.FailureResponse(result, "Schedule operation failed");
}
