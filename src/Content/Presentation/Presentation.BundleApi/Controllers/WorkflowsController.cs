using Application.AI.Common.CQRS.Workflows.Submit;
using Domain.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Presentation.BundleApi.Extensions;
using Presentation.BundleApi.Services;
using Presentation.Common.Extensions;

namespace Presentation.BundleApi.Controllers;

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
/// none passed into the command. The knowledge scope established by
/// <c>KnowledgeScopeMiddleware</c> from the authenticated principal is what the plan store stamps. In
/// this codebase an unscoped write is not a private record but a world-readable one, so the identity
/// check below rejects rather than proceeding when a principal carries nothing usable.
/// </para>
/// </remarks>
[ApiController]
[Route("api/workflows")]
[Authorize]
[EnableRateLimiting(BundleApiServiceCollectionExtensions.DefaultRateLimitPolicy)]
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

        // Identity is resolved solely through the shared extension, the single authority for caller
        // identity in this solution. A principal with nothing usable is rejected here rather than
        // allowed to reach a store that would read its null owner as "global".
        if (User.GetUserIdOrNull() is null)
        {
            return Problem(
                title: "Unauthorized",
                detail: "The authenticated principal carries no usable identity.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

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
