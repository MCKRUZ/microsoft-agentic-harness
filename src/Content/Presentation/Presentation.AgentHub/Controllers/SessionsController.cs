using Application.AI.Common.Interfaces;
using Domain.AI.Observability.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Presentation.AgentHub.Controllers;

/// <summary>
/// Exposes session observability data for the Dashboard SPA.
/// Provides paginated session lists and per-session detail views
/// including messages, tool executions, and safety events.
/// </summary>
[ApiController]
[Route("api/sessions")]
[Authorize]
public sealed class SessionsController : ControllerBase
{
    private readonly IObservabilityStore _store;

    /// <summary>Initialises the controller with its dependencies.</summary>
    public SessionsController(IObservabilityStore store) =>
        _store = store;

    /// <summary>
    /// Returns a paginated list of sessions, optionally filtered by status,
    /// ordered by most recent first.
    /// </summary>
    /// <param name="limit">Maximum number of sessions to return (1-200, default 50).</param>
    /// <param name="offset">Number of sessions to skip for pagination (default 0).</param>
    /// <param name="status">Optional status filter (e.g. "completed", "errored", "active").</param>
    /// <param name="since">Optional Unix epoch seconds lower bound on started_at.</param>
    /// <param name="until">Optional Unix epoch seconds upper bound on started_at.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Read-only list of <see cref="SessionRecord"/>.</returns>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SessionRecord>>> GetSessions(
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        [FromQuery] string? status = null,
        [FromQuery] long? since = null,
        [FromQuery] long? until = null,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        offset = Math.Max(offset, 0);

        DateTimeOffset? sinceDto = since.HasValue
            ? DateTimeOffset.FromUnixTimeSeconds(since.Value)
            : null;
        DateTimeOffset? untilDto = until.HasValue
            ? DateTimeOffset.FromUnixTimeSeconds(until.Value)
            : null;

        var sessions = await _store.GetSessionsAsync(limit, offset, status, sinceDto, untilDto, ct);
        return Ok(sessions);
    }

    /// <summary>
    /// Returns full detail for a single session including its messages,
    /// tool executions, and safety events.
    /// </summary>
    /// <param name="id">The session's database-assigned identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A composite object with <c>session</c>, <c>messages</c>, <c>tools</c>,
    /// and <c>safetyEvents</c> properties. Returns 404 if the session does not exist.
    /// </returns>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetSessionDetail(
        Guid id, CancellationToken ct = default)
    {
        var session = await _store.GetSessionByIdAsync(id, ct);
        if (session is null)
            return NotFound();

        var messagesTask = _store.GetSessionMessagesAsync(id, ct);
        var toolsTask = _store.GetSessionToolExecutionsAsync(id, ct);
        var safetyTask = _store.GetSessionSafetyEventsAsync(id, ct);

        await Task.WhenAll(messagesTask, toolsTask, safetyTask);

        return Ok(new
        {
            session,
            messages = messagesTask.Result,
            tools = toolsTask.Result,
            safetyEvents = safetyTask.Result
        });
    }
}
