using Application.AI.Common.Interfaces;
using Application.Common.Helpers;
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
/// <remarks>
/// These endpoints return <b>global, cross-user</b> observability data — the
/// underlying <see cref="IObservabilityStore"/> queries carry no caller identity,
/// so a single response can surface any user's conversation content, tool
/// args/stdout, and composed prompt bodies. That is privileged observability
/// data, not per-user data, so the whole controller is role-gated with
/// <see cref="ObserverRole"/> — the same app role that gates the equivalent
/// SignalR push (<c>AgentTelemetryHub.JoinGlobalTraces</c>). A plain
/// authenticated chat user (no role) gets 403 here, exactly as they do over
/// SignalR; without this gate any authenticated caller could enumerate and read
/// every user's conversations (horizontal-privilege IDOR).
/// </remarks>
[ApiController]
[Route("api/sessions")]
[Authorize(Roles = ObserverRole)]
public sealed class SessionsController : ControllerBase
{
    /// <summary>
    /// App role required to read the global session observability surface. Mirrors
    /// <c>AgentTelemetryHub.JoinGlobalTraces</c>'s <c>AgentHub.Traces.ReadAll</c>
    /// requirement so the HTTP and SignalR views of the same cross-user data enforce
    /// identical authorization.
    /// </summary>
    public const string ObserverRole = "AgentHub.Traces.ReadAll";

    /// <summary>
    /// How much of a rejected <c>status</c> value the 400 body repeats back. Long enough to identify
    /// a typo, short enough that the response cannot be used to amplify caller-supplied text.
    /// </summary>
    private const int MaxEchoedStatusLength = 32;

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
    /// <param name="status">
    /// Optional status filter: any member of <see cref="SessionStatus"/>, matched case-insensitively.
    /// Any other word is a 400 — see the remarks on the 400 response.
    /// <para>
    /// Deliberately not an enumeration of the accepted words. The list was written out here once and
    /// went stale the moment the vocabulary grew; the code below derives the accepted set from the
    /// enum, so there is nothing here left to keep in sync.
    /// </para>
    /// </param>
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
    /// <remarks>
    /// An unrecognised <c>status</c> is rejected rather than treated as "match nothing". The two are
    /// indistinguishable to a caller — both render as an empty list — and a filter that answers 200
    /// with no rows reads as "there are none of those", which is exactly how #289's unwritable
    /// statuses stayed invisible. A typo should say so.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<SessionListRowDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
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

        // EnumNameHelper, not Enum.TryParse: the latter accepts "2" and "Active,Error" and hands back
        // a value no row can hold.
        SessionStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!EnumNameHelper.TryParseName<SessionStatus>(status, out var parsed))
            {
                // The rejected value is echoed back because a diagnostic that will not say what it
                // read is a poor one — but truncated, because it is caller-controlled and otherwise
                // unbounded, and an error body is a cheap way to have a service repeat a megabyte
                // back at whoever sent it and into every log that records the response.
                var echoed = status.Length > MaxEchoedStatusLength
                    ? string.Concat(status.AsSpan(0, MaxEchoedStatusLength), "…")
                    : status;

                return BadRequest(new ProblemDetails
                {
                    Title = "Unknown session status.",
                    Detail =
                        $"'{echoed}' is not a session status. Expected one of: " +
                        $"{string.Join(", ", Enum.GetValues<SessionStatus>().Select(s => s.ToDbValue()))}.",
                    Status = StatusCodes.Status400BadRequest,
                });
            }

            statusFilter = parsed;
        }

        DateTimeOffset? sinceDto = since.HasValue
            ? DateTimeOffset.FromUnixTimeSeconds(since.Value)
            : null;
        DateTimeOffset? untilDto = until.HasValue
            ? DateTimeOffset.FromUnixTimeSeconds(until.Value)
            : null;

        var sessions = await _store.GetSessionsAsync(limit, offset, statusFilter, sinceDto, untilDto, ct);

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

    /// <summary>
    /// Returns full args + stdout for a single tool invocation, scoped to its
    /// parent session so a forged invocationId from a different session can't
    /// leak through. Powers the <c>/sessions/:id/tools/:invocationId</c>
    /// Foresight deep-link.
    /// </summary>
    /// <param name="id">Parent session id.</param>
    /// <param name="invocationId">Tool execution row id.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("{id:guid}/tools/{invocationId:guid}")]
    [ProducesResponseType(typeof(ToolInvocationDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ToolInvocationDetailDto>> GetToolInvocation(
        Guid id, Guid invocationId, CancellationToken ct = default)
    {
        var record = await _store.GetToolExecutionByIdAsync(id, invocationId, ct);
        if (record is null)
            return NotFound();

        return Ok(ToolInvocationDetailDto.From(record));
    }

    /// <summary>
    /// Returns the full message body for a single session message, scoped to
    /// its parent session. Powers the <c>/sessions/:id/files/:messageId</c>
    /// file-body Foresight deep-link.
    /// </summary>
    /// <param name="id">Parent session id.</param>
    /// <param name="messageId">Message row id.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("{id:guid}/messages/{messageId:guid}")]
    [ProducesResponseType(typeof(MessageBodyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MessageBodyDto>> GetMessageBody(
        Guid id, Guid messageId, CancellationToken ct = default)
    {
        var record = await _store.GetMessageByIdAsync(id, messageId, ct);
        if (record is null)
            return NotFound();

        return Ok(MessageBodyDto.From(record));
    }

    /// <summary>
    /// Returns the full body text for a single Foresight <c>LoadedItem</c> —
    /// composed system prompt, skill instructions, tool JSON schema, MCP
    /// descriptor, or sub-agent description. Scoped to the parent session so
    /// a forged turn/loaded index from a different session can't leak through.
    /// Returns 404 when the session doesn't exist, when the snapshot has no
    /// captured body for that index, or when the row predates body capture.
    /// </summary>
    /// <param name="id">Parent session id.</param>
    /// <param name="turnIndex">Turn the loaded item belongs to.</param>
    /// <param name="loadedIndex">Position in the snapshot's loaded[] array.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("{id:guid}/turns/{turnIndex:int}/loaded/{loadedIndex:int}/body")]
    [ProducesResponseType(typeof(LoadedBodyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LoadedBodyDto>> GetLoadedBody(
        Guid id, int turnIndex, int loadedIndex, CancellationToken ct = default)
    {
        // Bounds: indexes are non-negative; reject obviously malformed
        // requests at the boundary before doing the store hit.
        if (turnIndex < 0 || loadedIndex < 0)
            return NotFound();

        // Sidecar table is keyed by conversation id, but the public URL is
        // keyed by session id — resolve the conversation id from the session
        // first so a forged conversation id from another session can't return
        // someone else's prompt bodies.
        var session = await _store.GetSessionByIdAsync(id, ct);
        if (session is null || string.IsNullOrEmpty(session.ConversationId))
            return NotFound();

        var body = await _store.GetLoadedBodyAsync(
            session.ConversationId, turnIndex, loadedIndex, ct);
        if (body is null)
            return NotFound();

        return Ok(new LoadedBodyDto(
            session.ConversationId, turnIndex, loadedIndex, body));
    }
}
