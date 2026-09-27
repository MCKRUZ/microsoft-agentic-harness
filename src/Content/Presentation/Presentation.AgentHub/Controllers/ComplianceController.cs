using Application.Core.CQRS.Compliance.EraseMyData;
using Application.Core.CQRS.Compliance.GenerateComplianceReport;
using Application.Core.Compliance;
using Domain.AI.Compliance;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Presentation.Common.Extensions;

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
    /// Generates a compliance report over a time window — the append-only, hash-chained governance/
    /// change/egress/escalation/drift trails joined with sessions, safety-filter events, and audit-log
    /// entries from the conversation database, plus a live tamper-evidence check of every chain.
    /// </summary>
    /// <remarks>
    /// This is cross-user, privileged observability data — the same posture as <c>SessionsController</c>
    /// — so it is gated separately from the rest of this controller with
    /// <see cref="SessionsController.ObserverRole"/> rather than this controller's own bare
    /// <see cref="AuthorizeAttribute"/>, which only requires authentication.
    /// </remarks>
    /// <param name="start">Start of the reporting window, as Unix epoch seconds (inclusive).</param>
    /// <param name="end">End of the reporting window, as Unix epoch seconds (inclusive).</param>
    /// <param name="conversationId">Optional: narrows the session/safety/audit-log sections to one conversation.</param>
    /// <param name="maxRecordsPerSource">Optional: caps raw records returned per data source.</param>
    /// <param name="format">Either <c>json</c> (default) or <c>markdown</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Report generated; body is JSON or Markdown depending on <paramref name="format"/>.</response>
    /// <response code="400">Validation failure (e.g. window too long, end before start).</response>
    /// <response code="401">No authenticated user identity present.</response>
    /// <response code="403">Authenticated, but lacking the observer role.</response>
    [HttpGet("reports")]
    [Authorize(Roles = SessionsController.ObserverRole)]
    [ProducesResponseType(typeof(ComplianceReport), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GenerateReport(
        [FromQuery] long start,
        [FromQuery] long end,
        [FromQuery] string? conversationId = null,
        [FromQuery] int? maxRecordsPerSource = null,
        [FromQuery] string format = "json",
        CancellationToken cancellationToken = default)
    {
        if (User.GetUserIdOrNull() is not { } callerId)
            return this.NoUsableIdentity();

        var query = new GenerateComplianceReportQuery
        {
            CallerId = callerId,
            Start = DateTimeOffset.FromUnixTimeSeconds(start),
            End = DateTimeOffset.FromUnixTimeSeconds(end),
            ConversationId = string.IsNullOrWhiteSpace(conversationId) ? null : conversationId,
        };
        if (maxRecordsPerSource.HasValue)
            query = query with { MaxRecordsPerSource = maxRecordsPerSource.Value };

        var result = await _mediator.Send(query, cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
            return this.FailureResponse(result, "Compliance report generation failed");

        if (string.Equals(format, "markdown", StringComparison.OrdinalIgnoreCase))
            return Content(ComplianceReportMarkdownRenderer.Render(result.Value!), "text/markdown");

        return Ok(result.Value);
    }

    /// <summary>
    /// Maps a <see cref="Result{T}"/> onto an HTTP response, translating failure categories to status
    /// codes. Failure bodies are generic — handlers have already logged the real detail; the client never
    /// receives store internals, paths, or stack traces (per the harness error-response security rule).
    /// </summary>
    private IActionResult ToActionResult<T>(Result<T> result) =>
        result.IsSuccess
            ? Ok(result.Value)
            : this.FailureResponse(result, "Erasure failed");
}
