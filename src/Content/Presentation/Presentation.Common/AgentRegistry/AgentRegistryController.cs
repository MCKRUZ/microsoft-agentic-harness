using Application.Core.CQRS.Agents.RefreshAgentRegistry;
using Domain.AI.Agents;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Presentation.Common.Extensions;

namespace Presentation.Common.AgentRegistry;

/// <summary>
/// Operator API for the agent registry: force an immediate rescan of the configured
/// <c>AGENT.md</c> paths without a process restart (issue #705).
/// </summary>
/// <remarks>
/// <para>
/// <b>Opt-in mount.</b> The controller ships in <c>Presentation.Common</c> but its route only
/// exists in hosts that called <see cref="AgentRegistryApiMvcBuilderExtensions.AddAgentRegistryApi"/>
/// — see <see cref="RequiresAgentRegistryApiOptInAttribute"/>. Non-opted hosts answer this path
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
/// <para>
/// <b>Read path unaffected.</b> This controller does not duplicate <c>AgentsController</c>'s
/// <c>GET /api/agents</c> listing — refresh is a write, so it lives on its own opt-in surface; the
/// existing read endpoint already reflects whatever the registry currently holds, before or after a
/// refresh.
/// </para>
/// </remarks>
[ApiController]
[Route("api/agent-registry")]
[RequiresAgentRegistryApiOptIn]
public sealed class AgentRegistryController : ControllerBase
{
    /// <summary>
    /// App role required to force an agent registry refresh. A write — it forces every configured
    /// agent path to be rescanned — so it is held separately from ordinary agent read access.
    /// </summary>
    public const string OperateRole = "Harness.Agents.Operate";

    private readonly IMediator _mediator;

    /// <summary>Initializes the controller with its dependencies.</summary>
    /// <param name="mediator">The MediatR mediator used to dispatch the refresh command.</param>
    public AgentRegistryController(IMediator mediator)
    {
        ArgumentNullException.ThrowIfNull(mediator);
        _mediator = mediator;
    }

    /// <summary>
    /// Forces the agent registry to rescan its configured filesystem paths immediately. The caller
    /// identity from the token is recorded in the audit trail alongside the request.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A summary of what changed: added, updated, and removed agent ids, the new total, and the searched paths.</returns>
    /// <response code="200">The registry was refreshed; the body carries the change summary.</response>
    /// <response code="401">Caller is not authenticated, or carries no usable identity claim.</response>
    /// <response code="403">Caller lacks <see cref="OperateRole"/>.</response>
    [HttpPost("refresh")]
    [Authorize(Roles = OperateRole)]
    [ProducesResponseType(typeof(AgentRegistryRefreshResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Refresh(CancellationToken cancellationToken)
    {
        if (User.GetUserIdOrNull() is not { } callerId)
            return this.NoUsableIdentity();

        var result = await _mediator.Send(new RefreshAgentRegistryCommand
        {
            CallerId = callerId
        }, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? Ok(result.Value) : this.FailureResponse(result, "Agent registry refresh failed");
    }
}
