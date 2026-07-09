using Application.Core.CQRS.Compliance.EraseMyData;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Presentation.AgentHub.Controllers;

/// <summary>
/// REST API for data-subject compliance actions. Currently exposes self-scoped right-to-erasure.
/// </summary>
/// <remarks>
/// <para>
/// The controller is <see cref="AuthorizeAttribute">[Authorize]</see> (authentication required, no role
/// gate): erasing one's <b>own</b> data is a data-subject right available to every authenticated user,
/// not a privileged administrative operation. Anonymous callers are rejected by the framework before any
/// handler runs.
/// </para>
/// <para>
/// <b>Self-scope.</b> The erase endpoint takes no request body and no owner parameter. The subject of the
/// erasure is resolved server-side from the ambient knowledge scope (established from the caller's
/// identity claim by <c>KnowledgeScopeMiddleware</c>), so a caller can only ever erase their own data —
/// there is no field through which another owner's id could be supplied.
/// </para>
/// <para>
/// This action is intentionally <b>not</b> exposed as an agent-callable tool: data deletion must be
/// initiated by the human data subject through an authenticated request, never triggered autonomously by
/// an agent turn.
/// </para>
/// </remarks>
[ApiController]
[Route("api/compliance")]
[Authorize]
public sealed class ComplianceController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>Initializes the controller with its MediatR dependency.</summary>
    /// <param name="mediator">The MediatR mediator used to dispatch the erasure command.</param>
    public ComplianceController(IMediator mediator)
    {
        ArgumentNullException.ThrowIfNull(mediator);
        _mediator = mediator;
    }

    /// <summary>
    /// Erases all knowledge data owned by the authenticated caller — graph nodes/edges, feedback
    /// weights, and vector embeddings — and returns an <see cref="ErasureReceipt"/> as proof of
    /// compliance. The caller's identity is the sole subject; no other owner can be targeted.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The erasure receipt with actual deleted counts.</returns>
    /// <response code="200">Erasure completed; body is the receipt.</response>
    /// <response code="403">No authenticated user scope present.</response>
    /// <response code="500">Erasure failed; see server logs (details are not leaked to the caller).</response>
    [HttpPost("erase-my-data")]
    [ProducesResponseType(typeof(ErasureReceipt), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> EraseMyData(CancellationToken cancellationToken)
    {
        var result = await _mediator
            .Send(new EraseMyDataCommand(), cancellationToken)
            .ConfigureAwait(false);

        return ToActionResult(result);
    }

    /// <summary>
    /// Maps a <see cref="Result{T}"/> onto an HTTP response, translating failure categories to status
    /// codes. Failure bodies are generic — handlers have already logged the real detail; the client never
    /// receives store internals, paths, or stack traces (per the harness error-response security rule).
    /// </summary>
    private IActionResult ToActionResult<T>(Result<T> result)
    {
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        return result.FailureType switch
        {
            ResultFailureType.Validation => Problem(
                title: "Validation failed",
                detail: string.Join(" / ", result.Errors),
                statusCode: StatusCodes.Status400BadRequest),
            ResultFailureType.Unauthorized => Problem(
                title: "Unauthorized",
                detail: string.Join(" / ", result.Errors),
                statusCode: StatusCodes.Status401Unauthorized),
            ResultFailureType.Forbidden => Problem(
                title: "Forbidden",
                detail: string.Join(" / ", result.Errors),
                statusCode: StatusCodes.Status403Forbidden),
            _ => Problem(
                title: "Erasure failed",
                detail: "An error occurred processing the request. See server logs for details.",
                statusCode: StatusCodes.Status500InternalServerError),
        };
    }
}
