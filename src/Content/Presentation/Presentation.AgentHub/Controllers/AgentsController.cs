using Application.AI.Common.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Presentation.AgentHub.Extensions;
using Presentation.AgentHub.Interfaces;
using Presentation.AgentHub.DTOs;

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
    /// <summary>
    /// Synthetic agent returned by <see cref="GetAgents"/> when no <c>AGENT.md</c> manifests
    /// are discovered. Kept as a dev-mode fallback so the UI is never blank — the warning
    /// log on misconfiguration is the signal that real manifests are missing.
    /// </summary>
    internal static readonly AgentSummary FallbackAgent = new("default", "Default", "No agents configured");

    private readonly IConversationStore _store;
    private readonly IAgentMetadataRegistry _agentRegistry;
    private readonly ILogger<AgentsController> _logger;

    /// <summary>Initialises the controller with its dependencies.</summary>
    public AgentsController(
        IConversationStore store,
        IAgentMetadataRegistry agentRegistry,
        ILogger<AgentsController> logger)
    {
        _store = store;
        _agentRegistry = agentRegistry;
        _logger = logger;
    }

    /// <summary>Returns every agent discovered from the configured <c>AGENT.md</c> paths.</summary>
    /// <remarks>
    /// When discovery yields zero agents the controller logs a warning and returns a single
    /// synthetic <see cref="FallbackAgent"/> so the UI is never blank in dev. Production
    /// deployments should see the warning as a configuration smell, not a normal state.
    /// </remarks>
    [HttpGet("agents")]
    public IActionResult GetAgents()
    {
        var definitions = _agentRegistry.GetAll();

        if (definitions.Count == 0)
        {
            _logger.LogWarning(
                "No agents discovered in AppConfig.AI.Agents paths {Paths}; returning dev-mode fallback",
                _agentRegistry.SearchedPaths);
            return Ok(new[] { FallbackAgent });
        }

        var agents = definitions
            .Select(d => new AgentSummary(d.Id, d.Name, d.Description))
            .ToArray();
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
