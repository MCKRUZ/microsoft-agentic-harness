using Application.AI.Common.CQRS.Workflows.Submit;
using Domain.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Presentation.ExecutionApi.Extensions;
using Presentation.ExecutionApi.Services;
using Presentation.Common.Extensions;

namespace Presentation.ExecutionApi.Controllers;

/// <summary>
/// REST surface for submitting externally-authored workflows. A submission is admitted, mapped to an
/// executable plan, and stored under the caller's own scope; running it is a separate operation.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Submission and execution are deliberately separate rights.</strong> This endpoint stores a
/// workflow and nothing more. A caller who can author one therefore does not automatically hold the
/// right to spend the host's model and tool credentials running it, and the two can be authorized
/// independently.
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

    /// <summary>Initializes the controller with its MediatR dependency.</summary>
    public WorkflowsController(IMediator mediator)
    {
        ArgumentNullException.ThrowIfNull(mediator);
        _mediator = mediator;
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

    private IActionResult MapFailure(Result result) =>
        this.FailureResponse(result, "Workflow submission failed");
}
