using Application.AI.Common.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.Routing;
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
    private readonly IAgentRouter _agentRouter;
    private readonly IOptionsMonitor<AgentHubConfig> _config;
    private readonly ILogger<AgentsController> _logger;

    /// <summary>Initialises the controller with its dependencies.</summary>
    public AgentsController(
        IConversationStore store,
        IAgentMetadataRegistry agentRegistry,
        IAgentRouter agentRouter,
        IOptionsMonitor<AgentHubConfig> config,
        ILogger<AgentsController> logger)
    {
        _store = store;
        _agentRegistry = agentRegistry;
        _agentRouter = agentRouter;
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
    /// <remarks>
    /// The 403 comes from the store refusing the read, mapped by <c>GlobalExceptionMiddleware</c>.
    /// The owner comparison and its audit log used to sit here, in one of six copies across the host.
    /// </remarks>
    [HttpGet("conversations/{id}")]
    public async Task<IActionResult> GetConversation(string id, CancellationToken ct)
    {
        var record = await _store.GetAsync(id, User.GetUserId(), ct);
        return record is null ? NotFound() : Ok(record);
    }

    /// <summary>Deletes a conversation. 403 if not owned by caller. 204 on success.</summary>
    /// <remarks>
    /// One store call rather than read-then-delete: the previous shape checked the owner and deleted
    /// as two separate operations, so a conversation could in principle change between them. The
    /// 404 is preserved by the store reporting whether it deleted anything.
    /// </remarks>
    [HttpDelete("conversations/{id}")]
    public async Task<IActionResult> DeleteConversation(string id, CancellationToken ct)
    {
        var deleted = await _store.DeleteAsync(id, User.GetUserId(), ct);
        return deleted ? NoContent() : NotFound();
    }

    /// <summary>
    /// Creates a new conversation owned by the caller and returns its thread id. The dashboard agent
    /// panel calls this to obtain a thread before opening the AG-UI run stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Agent resolution, in order: an explicit <see cref="CreateConversationRequest.AgentName"/> wins
    /// outright (today's default — the dashboard always sends <c>dashboard-agent</c> unless the caller
    /// has explicitly opted into auto-routing). Otherwise, if the caller supplied
    /// <see cref="CreateConversationRequest.FirstMessage"/>, <see cref="IAgentRouter"/> attempts to
    /// pick an agent from it — this is the opt-in path; the router itself declines (returns
    /// <see langword="null"/>) whenever it isn't confident, rather than guessing. Anything that didn't
    /// resolve an agent falls back to <see cref="AgentHubConfig.DefaultAgentName"/>. A 400 is returned
    /// only when nothing above supplies a usable name, so a conversation is never created unassigned.
    /// </para>
    /// </remarks>
    [HttpPost("conversations")]
    public async Task<IActionResult> CreateConversation(
        [FromBody] CreateConversationRequest? request, CancellationToken ct)
    {
        var agentName = await ResolveAgentNameAsync(request, ct);

        if (string.IsNullOrWhiteSpace(agentName))
            return BadRequest(new { error = "An agent name is required (none supplied, routing declined, and no default configured)." });

        var userId = User.GetUserId();
        var record = await _store.CreateAsync(agentName, userId, conversationId: null, ct);

        _logger.LogInformation(
            "Created conversation {ConversationId} for user {UserId} bound to agent {AgentName}.",
            record.Id, userId, agentName);

        return CreatedAtAction(nameof(GetConversation), new { id = record.Id },
            new CreateConversationResponse(record.Id, record.AgentName));
    }

    /// <summary>See the resolution order documented on <see cref="CreateConversation"/>.</summary>
    private async Task<string?> ResolveAgentNameAsync(CreateConversationRequest? request, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request?.AgentName))
            return request!.AgentName!.Trim();

        if (!string.IsNullOrWhiteSpace(request?.FirstMessage))
        {
            var selection = await _agentRouter.RouteAsync(request!.FirstMessage!.Trim(), ct);
            if (selection is not null)
            {
                _logger.LogInformation(
                    "Routed conversation to agent {AgentId} (confidence {Confidence:F2}): {Reasoning}",
                    selection.SelectedAgent.AgentId, selection.ConfidenceScore, selection.Reasoning);
                return selection.SelectedAgent.AgentId;
            }
        }

        return _config.CurrentValue.DefaultAgentName;
    }

    /// <summary>
    /// Rebinds an existing conversation to a different agent — the explicit "re-route" a caller can
    /// invoke on a thread, since an agent is otherwise pinned for the conversation's whole lifetime
    /// once chosen. 404 if the conversation doesn't exist, 403 if not owned by the caller.
    /// </summary>
    /// <remarks>
    /// With an explicit <see cref="ReassignAgentRequest.AgentName"/>, that name is used directly — a
    /// manual override, not re-routed. Without one, <see cref="IAgentRouter"/> is re-run against the
    /// conversation's most recent user message; a 400 covers both the case where the router can't
    /// find one to route on and the case where it declines rather than guess.
    /// </remarks>
    [HttpPatch("conversations/{id}/agent")]
    public async Task<IActionResult> ReassignAgent(
        string id, [FromBody] ReassignAgentRequest? request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        var agentName = request?.AgentName?.Trim();

        if (string.IsNullOrWhiteSpace(agentName))
        {
            // Bounded tail read, not the full transcript — GetHistoryForDispatch is built for
            // exactly this "recent messages" access pattern, and a re-route only ever needs the
            // latest user turn, never the whole history of a conversation that may run for hundreds.
            var recentMessages = await _store.GetHistoryForDispatch(id, userId, maxMessages: 20, ct);
            if (recentMessages is null)
                return NotFound();

            var lastUserMessage = recentMessages.LastOrDefault(m => m.Role == MessageRole.User)?.Content;
            if (string.IsNullOrWhiteSpace(lastUserMessage))
                return BadRequest(new { error = "No target agent supplied and the conversation has no user message to route on." });

            var selection = await _agentRouter.RouteAsync(lastUserMessage, ct);
            if (selection is null)
                return BadRequest(new { error = "Routing declined to pick an agent; supply an explicit agentName instead." });

            agentName = selection.SelectedAgent.AgentId;
        }

        var updated = await _store.ReassignAgentAsync(id, userId, agentName, ct);
        if (updated is null)
            return NotFound();

        _logger.LogInformation(
            "Reassigned conversation {ConversationId} for user {UserId} to agent {AgentName}.",
            id, userId, agentName);

        return Ok(new CreateConversationResponse(updated.Id, updated.AgentName));
    }
}

/// <summary>Request body for <see cref="AgentsController.CreateConversation"/>.</summary>
/// <param name="AgentName">
/// The agent to bind the conversation to. Optional — takes priority over <see cref="FirstMessage"/>
/// and the configured default when supplied.
/// </param>
/// <param name="FirstMessage">
/// The user's opening message, used to auto-route to an agent when <see cref="AgentName"/> is
/// omitted. Optional and ignored when <see cref="AgentName"/> is supplied — this is the opt-in
/// signal for routing, not a default: a caller that wants today's fixed-agent behavior simply
/// omits it (or keeps sending an explicit <see cref="AgentName"/>).
/// </param>
public sealed record CreateConversationRequest(string? AgentName, string? FirstMessage = null);

/// <summary>Response for <see cref="AgentsController.CreateConversation"/>.</summary>
/// <param name="ThreadId">The new conversation's id, used as the AG-UI <c>threadId</c>.</param>
/// <param name="AgentName">The agent the conversation was bound to.</param>
public sealed record CreateConversationResponse(string ThreadId, string AgentName);

/// <summary>Request body for <see cref="AgentsController.ReassignAgent"/>.</summary>
/// <param name="AgentName">
/// The agent to rebind the conversation to. Optional — omit it to have <c>IAgentRouter</c> pick one
/// from the conversation's most recent user message instead of naming one explicitly.
/// </param>
public sealed record ReassignAgentRequest(string? AgentName = null);
