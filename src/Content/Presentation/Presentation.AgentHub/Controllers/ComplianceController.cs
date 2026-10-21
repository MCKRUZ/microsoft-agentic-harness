using Application.Core.CQRS.Compliance.EraseMyData;
using Application.Core.CQRS.Compliance.EraseSubjectData;
using Application.Core.CQRS.Compliance.GenerateComplianceReport;
using Application.Core.Compliance;
using Domain.AI.Compliance;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common;
using Domain.Common.Config.AI;
using MediatR;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Presentation.AgentHub.Auth;
using Presentation.Common.Extensions;

namespace Presentation.AgentHub.Controllers;

/// <summary>
/// REST API for data-subject compliance actions: self-scoped right-to-erasure, operator-initiated erasure, and
/// compliance reports.
/// </summary>
/// <remarks>
/// <para>
/// The controller is <see cref="AuthorizeAttribute">[Authorize]</see> (authentication required, no role
/// gate): erasing one's <b>own</b> data is a data-subject right available to every authenticated user,
/// not a privileged administrative operation. Anonymous callers are rejected by the framework before any
/// handler runs. Erasing someone else and reading reports each carry their own role.
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
    /// <summary>
    /// The role allowed to erase a named person's data on their request (#900). Grant it only to staff accountable for
    /// data-subject requests; no development identity carries it.
    /// </summary>
    public const string EraseSubjectRole = "Harness.Compliance.EraseSubject";

    private readonly IMediator _mediator;
    private readonly AuditedErasure _erasure;
    private readonly IOptionsMonitor<ErasureConfig> _erasureConfig;

    /// <summary>Initializes the controller.</summary>
    /// <param name="mediator">The MediatR mediator used to dispatch the erasure commands.</param>
    /// <param name="erasure">Records operator erasure requests refused before they reach a command.</param>
    /// <param name="erasureConfig">Erasure configuration: which tenants' operators may erase.</param>
    public ComplianceController(IMediator mediator, AuditedErasure erasure, IOptionsMonitor<ErasureConfig> erasureConfig)
    {
        ArgumentNullException.ThrowIfNull(mediator);
        ArgumentNullException.ThrowIfNull(erasure);
        ArgumentNullException.ThrowIfNull(erasureConfig);
        _mediator = mediator;
        _erasure = erasure;
        _erasureConfig = erasureConfig;
    }

    /// <summary>
    /// Erases everything the harness can link to the authenticated caller — knowledge graph, memories, document
    /// indexes, plans, schedules, conversations and the stores reached through them (#900) — and returns an
    /// <see cref="ErasureReceipt"/> listing what each store removed. The caller's identity is the sole subject; no other
    /// owner can be targeted.
    /// </summary>
    /// <remarks>
    /// Refused when sign-in is bypassed (<c>DevAuthHandler</c> or <c>SelfHostedAuthHandler</c>): every caller is then
    /// the same synthetic person, so "my data" is everyone's.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The erasure receipt with actual deleted counts.</returns>
    /// <response code="200">Erasure completed; body is the receipt.</response>
    /// <response code="403">No authenticated user scope present, or sign-in is bypassed.</response>
    /// <response code="500">Erasure failed; see server logs (details are not leaked to the caller).</response>
    [HttpPost("erase-my-data")]
    [ProducesResponseType(typeof(ErasureReceipt), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> EraseMyData(CancellationToken cancellationToken)
    {
        if (AuthBypassPolicy.IsSharedSyntheticIdentity(User))
        {
            await _erasure.RecordDeniedAsync(ErasureActor.DataSubject, nameof(EraseMyDataCommand), "sign-in is bypassed")
                .ConfigureAwait(false);
            return RefusedUnderBypass();
        }

        var result = await _mediator
            .Send(new EraseMyDataCommand(), cancellationToken)
            .ConfigureAwait(false);

        return ToActionResult(result);
    }

    /// <summary>
    /// Erases a named person's data on their request, for someone who cannot sign in to erase it themselves (#900), and
    /// returns the same <see cref="ErasureReceipt"/> the self-service endpoint does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The operator must hold <see cref="EraseSubjectRole"/>, which no development identity carries, and must sign in
    /// from a tenant listed in <c>AppConfig:AI:Erasure:OperatorTenantIds</c>; with that list empty, operator erasure is
    /// off. The role alone is not enough because each organisation's administrator assigns roles in their own tenant, and
    /// erasure reaches every tenant. Refused while sign-in is bypassed.
    /// </para>
    /// <para>
    /// Every refusal is recorded in the erasure audit trail, so a failed attempt to erase someone is visible. The role is
    /// therefore checked here rather than by an attribute, which would answer 403 before anything could be recorded. The
    /// operator is recorded by id; the person named only by pseudonymous reference.
    /// </para>
    /// <para>
    /// The subject travels in the body, not the URL, so it never lands in an access log. Supply it exactly as the person's
    /// identity provider issues it: conversations match their owner exactly, so a re-cased id would miss them.
    /// </para>
    /// </remarks>
    /// <param name="request">The person to erase.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The erasure receipt with actual deleted counts.</returns>
    /// <response code="200">Erasure completed; body is the receipt.</response>
    /// <response code="400">The subject id is missing, padded with whitespace, or too long.</response>
    /// <response code="401">No authenticated user identity present.</response>
    /// <response code="403">Lacking the erasure role, signed in from a tenant not allowed to erase, or sign-in is bypassed.</response>
    /// <response code="500">Erasure failed; see server logs (details are not leaked to the caller).</response>
    /// <response code="503">The erasure configuration loaded since startup is invalid.</response>
    [HttpPost("erasures")]
    [ProducesResponseType(typeof(ErasureReceipt), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> EraseSubjectData(
        [FromBody] EraseSubjectDataRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var operatorId = User.GetUserIdOrNull();
        var actor = operatorId is null ? ErasureActor.UnidentifiedOperator : ErasureActor.Operator(operatorId);
        if (OperatorRefusal(operatorId) is { } refusal)
        {
            await _erasure.RecordDeniedAsync(actor, nameof(EraseSubjectDataCommand), refusal.Reason, request.SubjectId)
                .ConfigureAwait(false);
            return refusal.Response;
        }

        var result = await _mediator
            .Send(new EraseSubjectDataCommand { SubjectId = request.SubjectId, OperatorId = operatorId! }, cancellationToken)
            .ConfigureAwait(false);

        // The validator runs before the handler, so its refusal is recorded here.
        if (result.FailureType == ResultFailureType.Validation)
        {
            await _erasure.RecordDeniedAsync(actor, nameof(EraseSubjectDataCommand), "invalid subject id", request.SubjectId)
                .ConfigureAwait(false);
        }

        return ToActionResult(result);
    }

    /// <summary>
    /// Why an operator may not erase, as a fixed phrase for the audit record and the response to send, or
    /// <see langword="null"/> when they may.
    /// </summary>
    private (string Reason, IActionResult Response)? OperatorRefusal(string? operatorId)
    {
        if (AuthBypassPolicy.IsSharedSyntheticIdentity(User))
            return ("sign-in is bypassed", RefusedUnderBypass());

        if (operatorId is null)
            return ("no usable operator identity", this.NoUsableIdentity());

        if (!User.IsInRole(EraseSubjectRole))
            return ("missing the erasure role", Forbid());

        List<string> allowedTenants;
        try
        {
            allowedTenants = _erasureConfig.CurrentValue.OperatorTenantIds;
        }
        catch (OptionsValidationException)
        {
            // An invalid section loaded after startup: refuse, closed, and say why in the audit trail.
            return ("erasure configuration invalid", Problem(
                detail: "Operator erasure is unavailable: its configuration is invalid.",
                statusCode: StatusCodes.Status503ServiceUnavailable));
        }

        if (allowedTenants.Count == 0)
        {
            return ("operator erasure is not configured", Problem(
                detail: "Operator erasure is off: no tenant is allowed to run it (AppConfig:AI:Erasure:OperatorTenantIds).",
                statusCode: StatusCodes.Status403Forbidden));
        }

        if (User.GetTenantId() is not { } tenantId || !allowedTenants.Contains(tenantId, StringComparer.OrdinalIgnoreCase))
        {
            return ("operator's tenant may not erase", Problem(
                detail: "Operators from this tenant may not erase other people's data.",
                statusCode: StatusCodes.Status403Forbidden));
        }

        return null;
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

    /// <summary>
    /// While sign-in is bypassed every caller is the same synthetic person, so self-service erasure would erase everyone
    /// and an operator erasure could not say which operator asked.
    /// </summary>
    private ObjectResult RefusedUnderBypass() =>
        Problem(
            detail: "Erasure is not available while sign-in is bypassed: every caller shares one identity, so it " +
                    "would erase every user's data.",
            statusCode: StatusCodes.Status403Forbidden);
}

/// <summary>Request body for <see cref="ComplianceController.EraseSubjectData"/>.</summary>
/// <param name="SubjectId">The id of the person to erase, exactly as their identity provider issues it.</param>
public sealed record EraseSubjectDataRequest(string SubjectId);
