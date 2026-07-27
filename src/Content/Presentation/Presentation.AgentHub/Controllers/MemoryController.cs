using Application.Core.CQRS.Memory;
using Domain.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Presentation.AgentHub.Controllers;

/// <summary>
/// REST API for the caller's cross-session memory: remember, recall, and forget facts.
/// Delegates all work to MediatR command/query handlers over <c>IKnowledgeMemory</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Self-scope.</b> No endpoint accepts a tenant or owner parameter. Identity is established by
/// <c>KnowledgeScopeMiddleware</c> from the authenticated caller's claims, and the memory service
/// namespaces every node id as <c>memory:{tenant}:{user}:{key}</c> — so callers can only ever
/// read, write, or forget their own facts. Erasure rights are inherited automatically because
/// every HTTP-written node is owner-stamped.
/// </para>
/// <para>
/// <b>Honest write outcomes.</b> Every write passes the memory write gate (prompt-injection scan,
/// trust classification). <c>POST</c> returns the gate's outcome — <c>Persisted</c>,
/// <c>Quarantined</c> (stored for audit, never recalled), or <c>Rejected</c> (not stored) — as a
/// 200 rather than masking it, so external writers know whether their fact will ever be served.
/// </para>
/// </remarks>
[ApiController]
[Route("api/memory")]
[Authorize]
public sealed class MemoryController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>Initializes the controller with its MediatR dependency.</summary>
    /// <param name="mediator">The MediatR mediator used to dispatch memory operations.</param>
    public MemoryController(IMediator mediator)
    {
        ArgumentNullException.ThrowIfNull(mediator);
        _mediator = mediator;
    }

    /// <summary>
    /// Stores a fact in the caller's cross-session memory and reports the write gate's decision.
    /// </summary>
    /// <param name="request">The fact to remember (key, content, optional entity type).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The honest write outcome.</returns>
    /// <response code="200">The gate evaluated the write; body reports Persisted / Quarantined / Rejected.</response>
    /// <response code="400">Invalid key, content, or entity type.</response>
    /// <response code="401">Caller is not authenticated.</response>
    [HttpPost]
    [ProducesResponseType(typeof(RememberMemoryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Remember(
        [FromBody] RememberMemoryRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new RememberMemoryCommand
        {
            Key = request.Key,
            Content = request.Content,
            EntityType = request.EntityType ?? RememberMemoryCommand.DefaultEntityType
        }, cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
            return MapFailure(result);

        return Ok(new RememberMemoryResponse(
            result.Value!.Outcome.ToString(), result.Value.Reason));
    }

    /// <summary>
    /// Searches the caller's cross-session memory. Quarantined facts are never returned.
    /// </summary>
    /// <param name="query">Natural-language or keyword query.</param>
    /// <param name="maxResults">Maximum number of results (1–50). Default 5.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Matching memory entries (possibly empty).</returns>
    /// <response code="200">Search completed (may contain zero results).</response>
    /// <response code="400">Missing query or out-of-range maxResults.</response>
    /// <response code="401">Caller is not authenticated.</response>
    [HttpGet("search")]
    [ProducesResponseType(typeof(IReadOnlyList<MemoryEntry>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Search(
        [FromQuery] string? query,
        [FromQuery] int maxResults = 5,
        CancellationToken cancellationToken = default)
    {
        var result = await _mediator.Send(new RecallMemoryQuery
        {
            Query = query ?? string.Empty,
            MaxResults = maxResults
        }, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? Ok(result.Value) : MapFailure(result);
    }

    /// <summary>
    /// Removes a fact from the caller's cross-session memory. Idempotent: forgetting a key that
    /// holds nothing succeeds, mirroring the harness's delete-unknown convention.
    /// </summary>
    /// <param name="key">The key of the memory entry to forget.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content on success.</returns>
    /// <response code="204">The key no longer holds a fact (deleted, or never existed).</response>
    /// <response code="400">Key is empty, too long, or outside the HTTP-addressable charset.</response>
    /// <response code="401">Caller is not authenticated.</response>
    [HttpDelete("{key}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Forget(
        string key,
        CancellationToken cancellationToken)
    {
        var result = await _mediator
            .Send(new ForgetMemoryCommand { Key = key }, cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess ? NoContent() : MapFailure(result);
    }

    /// <summary>
    /// Maps a failed <see cref="Result"/> onto an HTTP response, translating failure categories to
    /// status codes. General (500) failures return a generic body — handlers have already logged
    /// the real detail; the client never receives store internals, paths, or stack traces (per the
    /// harness error-response security rule).
    /// </summary>
    private IActionResult MapFailure(Result result) => result.FailureType switch
    {
        ResultFailureType.Validation => Problem(
            title: "Validation failed",
            detail: string.Join(" / ", result.Errors),
            statusCode: StatusCodes.Status400BadRequest),
        ResultFailureType.Unauthorized => Problem(
            title: "Unauthorized",
            detail: string.Join(" / ", result.Errors),
            statusCode: StatusCodes.Status401Unauthorized),
        ResultFailureType.Forbidden => Problem(
            title: "Forbidden",
            detail: string.Join(" / ", result.Errors),
            statusCode: StatusCodes.Status403Forbidden),
        ResultFailureType.NotFound => Problem(
            title: "Not found",
            detail: string.Join(" / ", result.Errors),
            statusCode: StatusCodes.Status404NotFound),
        _ => Problem(
            title: "Memory operation failed",
            detail: "An error occurred processing the request. See server logs for details.",
            statusCode: StatusCodes.Status500InternalServerError),
    };
}

/// <summary>Request body for <c>POST /api/memory</c>.</summary>
/// <param name="Key">Caller-chosen key for the fact (letters, digits, '.', '_', '-'; max 128).</param>
/// <param name="Content">The fact content to remember (max 32 KB).</param>
/// <param name="EntityType">Optional entity type for the graph node; null uses "Fact".</param>
public sealed record RememberMemoryRequest(string Key, string Content, string? EntityType = null);

/// <summary>
/// Response body for <c>POST /api/memory</c> — the write gate's honest decision.
/// </summary>
/// <param name="Outcome">
/// <c>"Persisted"</c> (stored, recallable), <c>"Quarantined"</c> (stored for audit, never
/// recalled), or <c>"Rejected"</c> (not stored).
/// </param>
/// <param name="Reason">The gate's short, audit-safe explanation (e.g. <c>"trusted"</c>).</param>
public sealed record RememberMemoryResponse(string Outcome, string Reason);
