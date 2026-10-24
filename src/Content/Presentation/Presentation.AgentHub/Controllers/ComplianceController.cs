using Application.Core.CQRS.Compliance.EraseMyData;
using Application.Core.CQRS.Compliance.EraseSubjectData;
using Application.Core.CQRS.Compliance.GenerateComplianceReport;
using Application.Core.CQRS.Compliance.RemoveDocument;
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
/// REST API for data-subject compliance actions: self-scoped right-to-erasure, operator-initiated erasure, operator
/// removal of a source document, and compliance reports.
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

    /// <summary>
    /// The role allowed to remove a source document, and the knowledge drawn from it, from the corpus (#901). A separate
    /// grant from erasure: removing content and erasing a person are different responsibilities.
    /// </summary>
    public const string RemoveDocumentRole = "Harness.Compliance.RemoveDocument";

    private static readonly OperatorAction Erasure = new(
        EraseSubjectRole, Name: "erasure", RoleName: "erasure", Verb: "erase", Detail: "erase other people's data");

    private static readonly OperatorAction DocumentRemoval = new(
        RemoveDocumentRole, Name: "document removal", RoleName: "document-removal", Verb: "remove documents",
        Detail: "remove documents from the corpus");

    private readonly IMediator _mediator;
    private readonly AuditedErasure _erasure;
    private readonly AuditedDocumentRemoval _documentRemoval;
    private readonly IOptionsMonitor<ErasureConfig> _erasureConfig;

    /// <summary>Initializes the controller.</summary>
    /// <param name="mediator">The MediatR mediator used to dispatch the erasure commands.</param>
    /// <param name="erasure">Records operator erasure requests refused before they reach a command.</param>
    /// <param name="documentRemoval">Records document-removal requests refused before they reach a command.</param>
    /// <param name="erasureConfig">Erasure configuration: which tenants' operators may erase or remove documents.</param>
    public ComplianceController(
        IMediator mediator,
        AuditedErasure erasure,
        AuditedDocumentRemoval documentRemoval,
        IOptionsMonitor<ErasureConfig> erasureConfig)
    {
        ArgumentNullException.ThrowIfNull(mediator);
        ArgumentNullException.ThrowIfNull(erasure);
        ArgumentNullException.ThrowIfNull(documentRemoval);
        ArgumentNullException.ThrowIfNull(erasureConfig);
        _mediator = mediator;
        _erasure = erasure;
        _documentRemoval = documentRemoval;
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
    /// the same synthetic person, so "my data" is everyone's. Refused for the same reason when the token is an
    /// application's own rather than a person's (<see cref="ClaimsPrincipalExtensions.IsApplicationOnly"/>, #907): every
    /// caller of that application shares its identity. Both refusals are recorded in the erasure audit trail.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The erasure receipt with actual deleted counts.</returns>
    /// <response code="200">Erasure completed; body is the receipt.</response>
    /// <response code="403">No authenticated user scope present, sign-in is bypassed, or the token is an application's.</response>
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

        if (User.IsApplicationOnly())
        {
            await _erasure.RecordDeniedAsync(ErasureActor.DataSubject, nameof(EraseMyDataCommand), "application token")
                .ConfigureAwait(false);
            return Problem(
                detail: "Erasing your own data needs a person's sign-in. An application token is shared by every caller of " +
                        "that application, so it would erase all of their data; ask an operator to erase a named person instead.",
                statusCode: StatusCodes.Status403Forbidden);
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
        if (OperatorRefusal(operatorId, Erasure) is { } refusal)
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
    /// Removes one source document from the corpus, with the knowledge drawn from it (#901), and returns a receipt of what
    /// each store removed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The operator must hold <see cref="RemoveDocumentRole"/>, which no development identity carries, and sign in from a
    /// tenant listed in <c>AppConfig:AI:Erasure:OperatorTenantIds</c> (the same trusted operators as erasure; empty means
    /// off). Refused while sign-in is bypassed. Every request that reaches this action, refused or not, is recorded in the
    /// audit trail with the document id; the role is checked here rather than by an attribute so a refusal can be recorded.
    /// A request turned away earlier (no sign-in, a body that does not parse, the rate limit) is not.
    /// </para>
    /// <para>
    /// A document id is derived from where the document came from, so the removal reaches every tenant and collection that
    /// loaded it. Its chunks leave every vector and full-text collection; in the knowledge graph and the GraphRAG corpus
    /// graph, knowledge only it produced goes, knowledge another document also produced survives, and community summaries
    /// built over it are deleted. This action is not, and must never become, an agent-callable tool.
    /// </para>
    /// </remarks>
    /// <param name="request">The document to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The receipt with what each store actually removed.</returns>
    /// <response code="200">Removal completed (check <c>completeness</c>); body is the receipt.</response>
    /// <response code="400">The document id is not 16 upper-case hexadecimal digits.</response>
    /// <response code="401">No authenticated user identity present.</response>
    /// <response code="403">Lacking the document-removal role, signed in from a tenant not allowed, or sign-in is bypassed.</response>
    /// <response code="500">Removal failed; see server logs (details are not leaked to the caller).</response>
    /// <response code="503">The erasure configuration loaded since startup is invalid.</response>
    [HttpPost("document-removals")]
    [ProducesResponseType(typeof(ErasureReceipt), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> RemoveDocument(
        [FromBody] RemoveDocumentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var operatorId = User.GetUserIdOrNull();
        var actor = operatorId is null ? ErasureActor.UnidentifiedOperator : ErasureActor.Operator(operatorId);
        if (OperatorRefusal(operatorId, DocumentRemoval) is { } refusal)
        {
            await _documentRemoval.RecordDeniedAsync(actor, refusal.Reason, request.DocumentId).ConfigureAwait(false);
            return refusal.Response;
        }

        var result = await _mediator
            .Send(new RemoveDocumentCommand { DocumentId = request.DocumentId, OperatorId = operatorId! }, cancellationToken)
            .ConfigureAwait(false);

        // The validator runs before the handler, so its refusal is recorded here.
        if (result.FailureType == ResultFailureType.Validation)
            await _documentRemoval.RecordDeniedAsync(actor, "invalid document id", request.DocumentId).ConfigureAwait(false);

        return result.IsSuccess ? Ok(result.Value) : this.FailureResponse(result, "Document removal failed");
    }

    /// <summary>
    /// Why an operator may not take <paramref name="action"/>, as a fixed phrase for the audit record and the response to
    /// send, or <see langword="null"/> when they may.
    /// </summary>
    private (string Reason, IActionResult Response)? OperatorRefusal(string? operatorId, OperatorAction action)
    {
        if (AuthBypassPolicy.IsSharedSyntheticIdentity(User))
            return ("sign-in is bypassed", RefusedUnderBypass(action));

        if (operatorId is null)
            return ("no usable operator identity", this.NoUsableIdentity());

        if (!User.IsInRole(action.Role))
            return ($"missing the {action.RoleName} role", Forbid());

        List<string> allowedTenants;
        try
        {
            allowedTenants = _erasureConfig.CurrentValue.OperatorTenantIds;
        }
        catch (OptionsValidationException)
        {
            // An invalid section loaded after startup: refuse, closed, and say why in the audit trail.
            return ($"{action.Name} configuration invalid", Problem(
                detail: $"Operator {action.Name} is unavailable: its configuration is invalid.",
                statusCode: StatusCodes.Status503ServiceUnavailable));
        }

        if (allowedTenants.Count == 0)
        {
            return ($"operator {action.Name} is not configured", Problem(
                detail: $"Operator {action.Name} is off: no tenant is allowed to run it (AppConfig:AI:Erasure:OperatorTenantIds).",
                statusCode: StatusCodes.Status403Forbidden));
        }

        if (User.GetTenantId() is not { } tenantId || !allowedTenants.Contains(tenantId, StringComparer.OrdinalIgnoreCase))
        {
            return ($"operator's tenant may not {action.Verb}", Problem(
                detail: $"Operators from this tenant may not {action.Detail}.",
                statusCode: StatusCodes.Status403Forbidden));
        }

        return null;
    }

    /// <summary>An operator-only action: the role it needs, and the words its refusals use.</summary>
    private sealed record OperatorAction(string Role, string Name, string RoleName, string Verb, string Detail);

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
    /// and an operator action could not say which operator asked.
    /// </summary>
    private ObjectResult RefusedUnderBypass(OperatorAction? action = null) =>
        Problem(
            detail: action == DocumentRemoval
                ? "Document removal is not available while sign-in is bypassed: every caller shares one identity, so " +
                  "the audit trail could not say which operator asked."
                : "Erasure is not available while sign-in is bypassed: every caller shares one identity, so it " +
                  "would erase every user's data.",
            statusCode: StatusCodes.Status403Forbidden);
}

/// <summary>Request body for <see cref="ComplianceController.EraseSubjectData"/>.</summary>
/// <param name="SubjectId">The id of the person to erase, exactly as their identity provider issues it.</param>
public sealed record EraseSubjectDataRequest(string SubjectId);

/// <summary>Request body for <see cref="ComplianceController.RemoveDocument"/>.</summary>
/// <param name="DocumentId">The document to remove: 16 upper-case hexadecimal digits, as ingestion forms it.</param>
public sealed record RemoveDocumentRequest(string DocumentId);
