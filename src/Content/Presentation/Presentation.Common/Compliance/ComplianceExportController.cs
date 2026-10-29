using Application.Core.Compliance;
using Application.Core.CQRS.Compliance.ExportMyData;
using Application.Core.CQRS.Compliance.ExportSubjectData;
using Domain.Common;
using Domain.Common.Compliance;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Presentation.Common.Extensions;

namespace Presentation.Common.Compliance;

/// <summary>
/// REST API for a person's right of access and portability (#902): a machine-readable copy of their own data, or of a
/// named person's, made by an operator on their behalf.
/// </summary>
/// <remarks>
/// <para>
/// <b>Delivered at once, never stored.</b> The copy is assembled for the request and returned as the response body, a
/// JSON file. An export job that wrote an archive for later download would create one more store of the person's whole
/// data, needing its own retention, erasure and access control, and a download link that must not be guessable. The cost
/// is that a very large copy is held in memory while the request runs; the request has the same long time limit as
/// erasure, each person may ask five times an hour, and the host assembles only
/// <see cref="ComplianceRateLimits.ConcurrentExportLimit"/> copies at once.
/// </para>
/// <para>
/// <b>One host at a time (#1003).</b> The copy holds what <em>this</em> process can find, and names the host it covers
/// (<see cref="PersonalDataExport.Host"/>). Hosts do not call one another, so a deployment that runs more than one host
/// holding personal data sends the request to each. Mounted only by
/// <see cref="ComplianceApiMvcBuilderExtensions.AddComplianceApi"/>.
/// </para>
/// <para>
/// <b>Kept apart from erasure in one process only.</b> A copy is not made while an erasure for the same person runs, through
/// the erasure journal's per-person turn, which is in-process like the journal's own hold. Behind several instances an
/// export can overlap an erasure on another one, and the copy may then miss what was being deleted.
/// </para>
/// <para>
/// Like erasure, this is <b>not</b> an agent-callable tool: it is started by the person through an authenticated request,
/// never by an agent turn. Responses are marked not to be cached anywhere.
/// </para>
/// </remarks>
[ApiController]
[Route("api/compliance")]
[Authorize]
[RequiresComplianceApiOptIn]
public sealed class ComplianceExportController : ControllerBase
{
    /// <summary>
    /// The role allowed to make a copy of a named person's data on their request (#902). A separate grant from erasure:
    /// reading someone's whole data and deleting it are different responsibilities. No development identity carries it.
    /// </summary>
    public const string ExportSubjectRole = "Harness.Compliance.ExportSubject";

    private static readonly OperatorAction ExportSubject = new(
        ExportSubjectRole, Name: "export", RoleName: "export", Verb: "export", Detail: "export other people's data",
        BypassDetail: "Operator export is not available while sign-in is bypassed: every caller shares one identity, so " +
                      "the audit trail could not say which operator asked.");

    private readonly IMediator _mediator;
    private readonly AuditedExport _export;
    private readonly OperatorGate _operatorGate;
    private readonly ComplianceApiMarker _api;

    /// <summary>Initializes the controller.</summary>
    /// <param name="mediator">The MediatR mediator used to dispatch the export command.</param>
    /// <param name="export">Records requests refused before they reach the command.</param>
    /// <param name="operatorGate">Decides whether an operator may export someone else's data.</param>
    /// <param name="api">The host's opt-in, which names the sign-in schemes every caller of which is the same person.</param>
    public ComplianceExportController(
        IMediator mediator, AuditedExport export, OperatorGate operatorGate, ComplianceApiMarker api)
    {
        ArgumentNullException.ThrowIfNull(mediator);
        ArgumentNullException.ThrowIfNull(export);
        ArgumentNullException.ThrowIfNull(operatorGate);
        ArgumentNullException.ThrowIfNull(api);
        _mediator = mediator;
        _export = export;
        _operatorGate = operatorGate;
        _api = api;
    }

    /// <summary>
    /// Returns a copy of everything the harness can find for the authenticated caller, with a manifest listing every store
    /// that can hold their data and what it returned or why it returned nothing. The caller's identity is the sole subject.
    /// </summary>
    /// <remarks>
    /// Refused when sign-in is bypassed (a scheme the host named in <see cref="ComplianceApiMarker.SharedIdentitySchemes"/>,
    /// such as AgentHub's <c>DevAuthHandler</c> or the Execution API's anonymous mode): every caller is then the same
    /// synthetic person, so "my data" is everyone's and the copy would disclose it all. Refused for the same reason
    /// when the token is an application's own rather than a person's (<see cref="ClaimsPrincipalExtensions.IsApplicationOnly"/>).
    /// Both refusals are recorded in the audit trail.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The copy, as a JSON file.</returns>
    /// <response code="200">The copy; check <c>isComplete</c> and the manifest for anything left out.</response>
    /// <response code="403">No authenticated user scope present, sign-in is bypassed, or the token is an application's.</response>
    /// <response code="500">The copy could not be made; see server logs (details are not leaked to the caller).</response>
    [HttpPost("export-my-data")]
    [ProducesResponseType(typeof(PersonalDataExport), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ExportMyData(CancellationToken cancellationToken)
    {
        if (_api.IsSharedSyntheticIdentity(User))
        {
            await _export.RecordDeniedAsync(ErasureActor.DataSubject, nameof(ExportMyDataCommand), "sign-in is bypassed")
                .ConfigureAwait(false);
            return Problem(
                detail: "A copy of your data is not available while sign-in is bypassed: every caller shares one identity, " +
                        "so it would contain every user's data.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (User.IsApplicationOnly())
        {
            await _export.RecordDeniedAsync(ErasureActor.DataSubject, nameof(ExportMyDataCommand), "application token")
                .ConfigureAwait(false);
            return Problem(
                detail: "A copy of your data needs a person's sign-in. An application token is shared by every caller of " +
                        "that application, so the copy would contain all of their data.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        var result = await _mediator.Send(new ExportMyDataCommand(), cancellationToken).ConfigureAwait(false);
        return AsAttachment(result);
    }

    /// <summary>
    /// Makes a copy of a named person's data on their request, for someone who cannot sign in to ask for it themselves
    /// (#902), and returns the same copy and manifest the self-service request does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The operator must hold <see cref="ExportSubjectRole"/>, which no development identity carries, and sign in from a
    /// tenant listed in <c>AppConfig:AI:Erasure:OperatorTenantIds</c> (the same trusted operators as erasure; empty means
    /// off). Refused while sign-in is bypassed. Every request that reaches this action, refused or not, is recorded in the
    /// audit trail: the operator by id, the person only by pseudonymous reference. The role is checked here rather than by
    /// an attribute so a refusal can be recorded. Each operator may make thirty an hour, and the copy counts against the
    /// host's limit on copies assembled at once.
    /// </para>
    /// <para>
    /// The subject travels in the body, not the URL, so it never lands in an access log. Supply it exactly as the
    /// person's identity provider issues it: conversations and document chunks match their owner exactly, so a re-cased
    /// id would return an empty copy. The copy is handed to the operator to pass on; it is never stored.
    /// </para>
    /// </remarks>
    /// <param name="request">The person whose copy to make.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The person's data as a JSON attachment.</returns>
    /// <response code="200">The copy; check its manifest's <c>isComplete</c>.</response>
    /// <response code="400">The subject id is missing, padded with whitespace, or too long.</response>
    /// <response code="401">No authenticated user identity present.</response>
    /// <response code="403">Lacking the export role, signed in from a tenant not allowed to export, or sign-in is bypassed.</response>
    /// <response code="500">The copy could not be made; see server logs (details are not leaked to the caller).</response>
    /// <response code="503">The operator configuration loaded since startup is invalid.</response>
    [HttpPost("exports")]
    [ProducesResponseType(typeof(PersonalDataExport), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ExportSubjectData(
        [FromBody] ExportSubjectDataRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var operatorId = User.GetUserIdOrNull();
        var actor = operatorId is null ? ErasureActor.UnidentifiedOperator : ErasureActor.Operator(operatorId);
        if (_operatorGate.Refusal(this, operatorId, ExportSubject) is { } refusal)
        {
            await _export.RecordDeniedAsync(actor, nameof(ExportSubjectDataCommand), refusal.Reason, request.SubjectId)
                .ConfigureAwait(false);
            return refusal.Response;
        }

        var result = await _mediator
            .Send(new ExportSubjectDataCommand { SubjectId = request.SubjectId, OperatorId = operatorId! }, cancellationToken)
            .ConfigureAwait(false);

        // The validator runs before the handler, so its refusal is recorded here.
        if (result.FailureType == ResultFailureType.Validation)
        {
            await _export.RecordDeniedAsync(actor, nameof(ExportSubjectDataCommand), "invalid subject id", request.SubjectId)
                .ConfigureAwait(false);
        }

        return AsAttachment(result);
    }

    /// <summary>The copy as a JSON file that nothing between here and the requester may keep, or the failure.</summary>
    private IActionResult AsAttachment(Result<PersonalDataExport> result)
    {
        if (!result.IsSuccess)
            return this.FailureResponse(result, "Export failed");

        Response.Headers.CacheControl = "no-store";
        Response.Headers.ContentDisposition =
            $"attachment; filename=\"personal-data-{result.Value!.GeneratedAt:yyyyMMdd'T'HHmmss'Z'}.json\"";
        return Ok(result.Value);
    }
}

/// <summary>Request body for <see cref="ComplianceExportController.ExportSubjectData"/>.</summary>
/// <param name="SubjectId">The id of the person whose copy to make, exactly as their identity provider issues it.</param>
public sealed record ExportSubjectDataRequest(string SubjectId);
