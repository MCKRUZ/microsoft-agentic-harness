using Application.AI.Common.Interfaces;
using Domain.AI.Context;
using Domain.AI.Observability.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Presentation.AgentHub.DTOs;
using Presentation.AgentHub.Extensions;

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
    /// <returns>
    /// Read-only list of session rows. Each row carries the
    /// <see cref="CategoryBreakdownDto"/> from the conversation's latest
    /// Foresight context snapshot (PR 3) — sessions without snapshots omit
    /// the field. Populated via a single batched
    /// <see cref="IObservabilityStore.GetLatestBreakdownsAsync"/> call, not
    /// N+1.
    /// </returns>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<SessionListRowDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<SessionListRowDto>>> GetSessions(
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

        // Single batched lookup: one DB hit for the whole page. Rows without a
        // snapshot are omitted by the store, so missing keys mean "no breakdown
        // yet" — the DTO carries null in that case and the frontend hides the
        // mini-bar.
        var conversationIds = sessions
            .Select(s => s.ConversationId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct()
            .ToArray();

        IReadOnlyDictionary<string, CategoryBreakdown> breakdowns =
            conversationIds.Length == 0
                ? new Dictionary<string, CategoryBreakdown>()
                : await _store.GetLatestBreakdownsAsync(conversationIds, ct);

        // SessionListRowDto.From mirrors every SessionRecord property by name —
        // a future SessionRecord rename surfaces as a compile error here rather
        // than a silent wire shape drift. The dictionary lookup guards null
        // ConversationId at the projection (TryGetValue on a Dictionary<string>
        // throws on a null key — defence-in-depth even though SessionRecord
        // declares ConversationId as required).
        var rows = sessions
            .Select(s => SessionListRowDto.From(
                s,
                !string.IsNullOrEmpty(s.ConversationId)
                    && breakdowns.TryGetValue(s.ConversationId, out var b)
                        ? b.ToDto()
                        : null))
            .ToArray();

        return Ok(rows);
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
        var snapshotsTask = _store.GetSnapshotsAsync(session.ConversationId, ct);

        await Task.WhenAll(messagesTask, toolsTask, safetyTask, snapshotsTask);

        var snapshots = snapshotsTask.Result.Select(s => s.ToDto()).ToArray();
        var breakdown = snapshots.Length > 0
            ? snapshots[^1].CtxAfter
            : null;

        return Ok(new
        {
            session,
            messages = messagesTask.Result,
            tools = toolsTask.Result,
            safetyEvents = safetyTask.Result,
            snapshots,
            breakdown,
        });
    }
}
