using Application.Core.CQRS.Compliance.GenerateComplianceReport;
using Application.Core.Compliance;
using Domain.AI.Compliance;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Presentation.Common.Extensions;

namespace Presentation.AgentHub.Controllers;

/// <summary>
/// REST API for compliance reports over AgentHub's audit trails and conversation database.
/// </summary>
/// <remarks>
/// Erasure, document removal and export live in the shared compliance API that every host holding personal data mounts
/// (<c>Presentation.Common.Compliance</c>, #1003); reports stay here because they read AgentHub's own conversation database
/// and observability trails.
/// </remarks>
[ApiController]
[Route("api/compliance")]
[Authorize]
public sealed class ComplianceReportsController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>Initializes the controller.</summary>
    /// <param name="mediator">The MediatR mediator used to dispatch the report query.</param>
    public ComplianceReportsController(IMediator mediator)
    {
        ArgumentNullException.ThrowIfNull(mediator);
        _mediator = mediator;
    }

    /// <summary>
    /// Generates a compliance report over a time window — the append-only, hash-chained governance/
    /// change/egress/escalation/drift trails joined with sessions, safety-filter events, and audit-log
    /// entries from the conversation database, plus a live tamper-evidence check of every chain.
    /// </summary>
    /// <remarks>
    /// This is cross-user, privileged observability data — the same posture as <c>SessionsController</c>
    /// — so it is gated with <see cref="SessionsController.ObserverRole"/> rather than the controller's own bare
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
}
