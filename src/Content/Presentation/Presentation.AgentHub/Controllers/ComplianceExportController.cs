using Application.Core.Compliance;
using Application.Core.CQRS.Compliance.ExportMyData;
using Domain.Common.Compliance;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Presentation.AgentHub.Auth;
using Presentation.Common.Extensions;

namespace Presentation.AgentHub.Controllers;

/// <summary>
/// REST API for a person's right of access and portability (#902): a machine-readable copy of their own data.
/// </summary>
/// <remarks>
/// <para>
/// <b>Delivered at once, never stored.</b> The copy is assembled for the request and returned as the response body, a
/// JSON file. An export job that wrote an archive for later download would create one more store of the person's whole
/// data, needing its own retention, erasure and access control, and a download link that must not be guessable. The cost
/// is that a very large copy is held in memory while the request runs; the request has the same long time limit as
/// erasure, each person may ask five times an hour, and the host assembles only
/// <see cref="DependencyInjection.ConcurrentExportLimit"/> copies at once.
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
public sealed class ComplianceExportController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly AuditedExport _export;

    /// <summary>Initializes the controller.</summary>
    /// <param name="mediator">The MediatR mediator used to dispatch the export command.</param>
    /// <param name="export">Records requests refused before they reach the command.</param>
    public ComplianceExportController(IMediator mediator, AuditedExport export)
    {
        ArgumentNullException.ThrowIfNull(mediator);
        ArgumentNullException.ThrowIfNull(export);
        _mediator = mediator;
        _export = export;
    }

    /// <summary>
    /// Returns a copy of everything the harness can find for the authenticated caller, with a manifest listing every store
    /// that can hold their data and what it returned or why it returned nothing. The caller's identity is the sole subject.
    /// </summary>
    /// <remarks>
    /// Refused when sign-in is bypassed (<c>DevAuthHandler</c> or <c>SelfHostedAuthHandler</c>): every caller is then the
    /// same synthetic person, so "my data" is everyone's and the copy would disclose it all. Refused for the same reason
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
        if (AuthBypassPolicy.IsSharedSyntheticIdentity(User))
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
        if (!result.IsSuccess)
            return this.FailureResponse(result, "Export failed");

        Response.Headers.CacheControl = "no-store";
        Response.Headers.ContentDisposition =
            $"attachment; filename=\"personal-data-{result.Value!.GeneratedAt:yyyyMMdd'T'HHmmss'Z'}.json\"";
        return Ok(result.Value);
    }
}
