using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Presentation.AgentHub.Extensions;
using Presentation.AgentHub.Interfaces;
using Presentation.AgentHub.Models;

namespace Presentation.AgentHub.Controllers;

/// <summary>
/// Manages agent discovery and conversation history.
/// All endpoints require authentication. Ownership is enforced at the conversation level:
/// a user may only access or delete conversations where <see cref="ConversationRecord.UserId"/>
/// matches their own identity claim.
/// </summary>
[ApiController]
[Route("api")]
[Authorize]
public sealed class AgentsController : ControllerBase
{
    private readonly IConversationStore _store;
    private readonly AgentHubConfig _config;
    private readonly ILogger<AgentsController> _logger;

    /// <summary>Initialises the controller with its dependencies.</summary>
    public AgentsController(
        IConversationStore store,
        IOptions<AgentHubConfig> config,
        ILogger<AgentsController> logger)
    {
        _store = store;
        _config = config.Value;
        _logger = logger;
    }

    /// <summary>Returns the list of configured agents.</summary>
    /// <remarks>
    /// TODO: Enumerate the full agent registry once the agent framework exposes it.
    /// For now returns a single summary derived from <see cref="AgentHubConfig.DefaultAgentName"/>.
    /// </remarks>
    [HttpGet("agents")]
    public IActionResult GetAgents()
    {
        var agents = new[] { new AgentSummary(_config.DefaultAgentName, "Default agent") };
        return Ok(agents);
    }

    /// <summary>Returns all conversations owned by the current user.</summary>
    [HttpGet("conversations")]
    public async Task<IActionResult> GetConversations(CancellationToken ct)
    {
        var userId = User.GetUserId();
        var conversations = await _store.ListAsync(userId, ct);
        return Ok(conversations);
    }

    /// <summary>Returns a single conversation. 404 if not found. 403 if not owned by caller.</summary>
    [HttpGet("conversations/{id}")]
    public async Task<IActionResult> GetConversation(string id, CancellationToken ct)
    {
        var record = await _store.GetAsync(id, ct);
        if (record is null)
            return NotFound();

        var callerId = User.GetUserId();
        if (record.UserId != callerId)
        {
            // Log both caller and owner IDs — intentional audit trail for IDOR attempts.
            _logger.LogWarning("User {UserId} attempted to access conversation {ConversationId} owned by {OwnerId}.",
                callerId, id, record.UserId);
            return Forbid();
        }
        return Ok(record);
    }

    /// <summary>Deletes a conversation. 403 if not owned by caller. 204 on success.</summary>
    [HttpDelete("conversations/{id}")]
    public async Task<IActionResult> DeleteConversation(string id, CancellationToken ct)
    {
        var record = await _store.GetAsync(id, ct);
        if (record is null)
            return NotFound();

        var callerId = User.GetUserId();
        if (record.UserId != callerId)
        {
            // Log both caller and owner IDs — intentional audit trail for IDOR attempts.
            _logger.LogWarning("User {UserId} attempted to delete conversation {ConversationId} owned by {OwnerId}.",
                callerId, id, record.UserId);
            return Forbid();
        }

        await _store.DeleteAsync(id, ct);
        return NoContent();
    }
}
