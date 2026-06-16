using Application.AI.Common.CQRS.Evaluation.GetEvalRunDetail;
using Application.AI.Common.CQRS.Evaluation.GetEvalRunHistory;
using Application.AI.Common.CQRS.Evaluation.GetPromptVersionComparison;
using Application.AI.Common.CQRS.Evaluation.GetTopRegressedCases;
using Application.AI.Common.CQRS.Evaluation.IngestEvalRun;
using Application.AI.Common.CQRS.Prompts.ReplayTraceWithPromptVersion;
using Domain.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Presentation.AgentHub.Controllers;

/// <summary>
/// REST API for the eval dashboard (Sub-phase 5.4.4): ingest, history, detail,
/// prompt-version comparison, regression diff, and replay. All endpoints
/// dispatch via MediatR so behaviors (validation, auth, audit) wrap every call.
/// </summary>
/// <remarks>
/// These endpoints expose <b>global, cross-user</b> evaluation data — run
/// reports, prompt-version comparisons, regressed cases, and trace replays carry
/// no caller identity, so any authenticated caller could otherwise enumerate and
/// read every team's eval results and the prompt/trace bodies behind them
/// (horizontal-privilege IDOR). The whole controller is therefore role-gated with
/// <see cref="SessionsController.ObserverRole"/> — the same app role that gates
/// the equivalent privileged session-observability surface
/// (<see cref="SessionsController"/>) and SignalR global-traces push. A plain
/// authenticated chat user (no role) gets 403 here, exactly as on those paths.
/// </remarks>
[ApiController]
[Route("api/evals")]
[Authorize(Roles = SessionsController.ObserverRole)]
public sealed class EvalController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>Initializes the controller with its MediatR dependency.</summary>
    public EvalController(IMediator mediator)
    {
        ArgumentNullException.ThrowIfNull(mediator);
        _mediator = mediator;
    }

    /// <summary>
    /// Persists an <see cref="Domain.AI.Evaluation.EvalRunReport"/> emitted by the
    /// EvalRunner CLI. Idempotent on <c>RunId</c>: a re-ingest of the same run
    /// returns 200 with <see cref="IngestEvalRunResult.Inserted"/> set to false.
    /// </summary>
    [HttpPost("ingest")]
    [ProducesResponseType(typeof(IngestEvalRunResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Ingest(
        [FromBody] IngestEvalRunCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var result = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return ToActionResult(result);
    }

    /// <summary>Returns the most recent eval runs as a summary list.</summary>
    /// <param name="take">Maximum rows to return. Defaults to 50; capped at 500 by the validator.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    [HttpGet("runs")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetHistory(
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await _mediator
            .Send(new GetEvalRunHistoryQuery { Take = take }, cancellationToken)
            .ConfigureAwait(false);
        return ToActionResult(result);
    }

    /// <summary>Returns the full report for one run, or 404 when the run is unknown.</summary>
    [HttpGet("runs/{runId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDetail(
        string runId,
        CancellationToken cancellationToken)
    {
        var result = await _mediator
            .Send(new GetEvalRunDetailQuery { RunId = runId }, cancellationToken)
            .ConfigureAwait(false);
        return ToActionResult(result);
    }

    /// <summary>
    /// Aggregates eval score per prompt version for the supplied prompt name.
    /// Powers the dashboard's prompt A/B comparison view.
    /// </summary>
    [HttpGet("prompts/{promptName}/compare")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ComparePromptVersions(
        string promptName,
        CancellationToken cancellationToken)
    {
        var result = await _mediator
            .Send(new GetPromptVersionComparisonQuery { PromptName = promptName }, cancellationToken)
            .ConfigureAwait(false);
        return ToActionResult(result);
    }

    /// <summary>
    /// Returns cases whose aggregated score dropped most between a baseline run
    /// and a current run, ordered by largest negative delta.
    /// </summary>
    [HttpGet("regressions")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRegressions(
        [FromQuery] string current,
        [FromQuery] string baseline,
        [FromQuery] int take = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await _mediator.Send(new GetTopRegressedCasesQuery
        {
            CurrentRunId = current,
            BaselineRunId = baseline,
            Take = take,
        }, cancellationToken).ConfigureAwait(false);
        return ToActionResult(result);
    }

    /// <summary>
    /// Replays a historical trace against a different prompt version
    /// (forwarded to Sub-phase 5.3b's <see cref="ReplayTraceWithPromptVersionCommand"/>).
    /// Caller supplies the original variables and target version.
    /// </summary>
    [HttpPost("replay")]
    [ProducesResponseType(typeof(PromptReplayResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Replay(
        [FromBody] ReplayTraceWithPromptVersionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var result = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return ToActionResult(result);
    }

    /// <summary>
    /// Maps a <see cref="Result{T}"/> outcome to an MVC <see cref="IActionResult"/>:
    /// Success → 200; NotFound → 404; Validation → 400; everything else → 500.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Error message exposure rules.</b> Validation and NotFound messages are
    /// safe to surface verbatim — they originate from validator strings and "not
    /// found" lookups, never from internal exceptions. Auth failures expose the
    /// declared reason. General (500) failures DO NOT pass <c>result.Errors</c>
    /// through to the client — handler code path is
    /// <c>catch (Exception ex) { Result.Fail($"...: {ex.Message}") }</c>, so the
    /// errors list contains raw <see cref="Exception.Message"/> which on SQLite/EF
    /// leaks SQL fragments, schema info, and file paths. Per the harness security
    /// rules: "Error responses: never leak stack traces, internal paths, or
    /// sensitive data." Handlers have already logged the original failure with
    /// structured detail; the client gets only a generic correlation message.
    /// </para>
    /// <para>
    /// Kept inline rather than in a shared helper because no other controller
    /// currently consumes <see cref="Result{T}"/> directly. Promote to a shared
    /// extension when a second consumer appears (matches the Phase 5.4.1
    /// "no extraction until N=3" precedent for the ValueConverter).
    /// </para>
    /// </remarks>
    private IActionResult ToActionResult<T>(Result<T> result)
    {
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        return result.FailureType switch
        {
            ResultFailureType.NotFound => Problem(
                title: "Not Found",
                detail: string.Join(" / ", result.Errors),
                statusCode: StatusCodes.Status404NotFound),
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
            _ => Problem(
                title: "Eval operation failed",
                detail: "An error occurred processing the request. See server logs for details.",
                statusCode: StatusCodes.Status500InternalServerError),
        };
    }
}
