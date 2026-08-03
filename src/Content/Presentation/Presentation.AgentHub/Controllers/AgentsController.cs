using Application.AI.Common.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Models.Conversations;
using Presentation.AgentHub.Config;
using Presentation.Common.Extensions;
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
    private readonly IOptionsMonitor<AgentHubConfig> _config;
    private readonly ILogger<AgentsController> _logger;

    /// <summary>Initialises the controller with its dependencies.</summary>
    public AgentsController(
        IConversationStore store,
        IAgentMetadataRegistry agentRegistry,
        IOptionsMonitor<AgentHubConfig> config,
        ILogger<AgentsController> logger)
    {
        _store = store;
        _agentRegistry = agentRegistry;
        _config = config;
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

    /// <summary>
    /// Creates a new conversation owned by the caller and returns its thread id. The dashboard agent
    /// panel calls this to obtain a thread before opening the AG-UI run stream.
    /// </summary>
    /// <remarks>
    /// The agent is taken from the request body, falling back to
    /// <see cref="AgentHubConfig.DefaultAgentName"/> when omitted. A 400 is returned when neither
    /// supplies an agent name, so a conversation is never created against an unspecified agent.
    /// </remarks>
    [HttpPost("conversations")]
    public async Task<IActionResult> CreateConversation(
        [FromBody] CreateConversationRequest? request, CancellationToken ct)
    {
        var agentName = !string.IsNullOrWhiteSpace(request?.AgentName)
            ? request!.AgentName!.Trim()
            : _config.CurrentValue.DefaultAgentName;

        if (string.IsNullOrWhiteSpace(agentName))
            return BadRequest(new { error = "An agent name is required (none supplied and no default configured)." });

        var userId = User.GetUserId();
        var record = await _store.CreateAsync(agentName, userId, conversationId: null, ct);

        _logger.LogInformation(
            "Created conversation {ConversationId} for user {UserId} bound to agent {AgentName}.",
            record.Id, userId, agentName);

        return CreatedAtAction(nameof(GetConversation), new { id = record.Id },
            new CreateConversationResponse(record.Id, record.AgentName));
    }
}

/// <summary>Request body for <see cref="AgentsController.CreateConversation"/>.</summary>
/// <param name="AgentName">
/// The agent to bind the conversation to. Optional — falls back to the configured default agent.
/// </param>
public sealed record CreateConversationRequest(string? AgentName);

/// <summary>Response for <see cref="AgentsController.CreateConversation"/>.</summary>
/// <param name="ThreadId">The new conversation's id, used as the AG-UI <c>threadId</c>.</param>
/// <param name="AgentName">The agent the conversation was bound to.</param>
public sealed record CreateConversationResponse(string ThreadId, string AgentName);
