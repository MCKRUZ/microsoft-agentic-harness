using Application.Core.CQRS.Skills.RefreshSkillRegistry;
using Domain.AI.Skills;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Presentation.Common.Extensions;

namespace Presentation.Common.SkillRegistry;

/// <summary>
/// Operator API for the skill registry: force an immediate rescan of the configured
/// <c>SKILL.md</c> paths without a process restart (issue #709, mirroring
/// <c>Presentation.Common.AgentRegistry.AgentRegistryController</c> from issue #705).
/// </summary>
/// <remarks>
/// <para>
/// <b>Opt-in mount.</b> The controller ships in <c>Presentation.Common</c> but its route only
/// exists in hosts that called <see cref="SkillRegistryApiMvcBuilderExtensions.AddSkillRegistryApi"/>
/// — see <see cref="RequiresSkillRegistryApiOptInAttribute"/>. Non-opted hosts answer this path
/// with a plain 404 before authentication.
/// </para>
/// <para>
/// <b>Identity from token, never from body.</b> The request carries no caller-id field; the caller
/// identity recorded in the audit trail always comes from the authenticated principal's stable
/// identity claim (<see cref="ClaimsPrincipalExtensions.GetUserIdOrNull"/> — the same resolver every
/// other ownership-sensitive surface in this harness uses, so a refresh cannot be attributed to
/// someone else and a host cannot end up attributing this action under a different identity than its
/// other subsystems use for the same principal).
/// </para>
/// </remarks>
[ApiController]
[Route("api/skill-registry")]
[RequiresSkillRegistryApiOptIn]
public sealed class SkillRegistryController : ControllerBase
{
    /// <summary>
    /// App role required to force a skill registry refresh. A write — it forces every configured
    /// skill path to be rescanned — so it is held separately from ordinary skill read access.
    /// </summary>
    public const string OperateRole = "Harness.Skills.Operate";

    private readonly IMediator _mediator;

    /// <summary>Initializes the controller with its dependencies.</summary>
    /// <param name="mediator">The MediatR mediator used to dispatch the refresh command.</param>
    public SkillRegistryController(IMediator mediator)
    {
        ArgumentNullException.ThrowIfNull(mediator);
        _mediator = mediator;
    }

    /// <summary>
    /// Forces the skill registry to rescan its configured filesystem paths immediately. The caller
    /// identity from the token is recorded in the audit trail alongside the request.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A summary of what changed: added, updated, and removed skill ids, the new total, and the searched paths.</returns>
    /// <response code="200">The registry was refreshed; the body carries the change summary.</response>
    /// <response code="401">Caller is not authenticated, or carries no usable identity claim.</response>
    /// <response code="403">Caller lacks <see cref="OperateRole"/>.</response>
    [HttpPost("refresh")]
    [Authorize(Roles = OperateRole)]
    [ProducesResponseType(typeof(SkillRegistryRefreshResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Refresh(CancellationToken cancellationToken)
    {
        if (User.GetUserIdOrNull() is not { } callerId)
            return this.NoUsableIdentity();

        var result = await _mediator.Send(new RefreshSkillRegistryCommand
        {
            CallerId = callerId
        }, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? Ok(result.Value) : this.FailureResponse(result, "Skill registry refresh failed");
    }
}
