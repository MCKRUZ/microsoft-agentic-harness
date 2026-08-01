using Application.AI.Common.Evaluation.Models;
using Application.AI.Common.Interfaces.Evaluation;
using Application.AI.Common.Interfaces.Governance;
using Application.Core.CQRS.Evaluation.Runs;
using Domain.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Presentation.Common.Extensions;
using Presentation.ExecutionApi.DTOs;
using Presentation.ExecutionApi.Extensions;

namespace Presentation.ExecutionApi.Controllers;

/// <summary>
/// REST surface for running the host's evaluation suites. A run is accepted, queued on the shared run
/// substrate, and polled for its report.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A caller names datasets; it never supplies paths or content.</strong> What may be evaluated
/// is whatever an operator placed in <c>AppConfig:AI:Evaluation:DatasetRoots</c>, and the mapping from
/// a name to a file happens server-side through <c>IEvalDatasetCatalog</c>. This is the whole
/// authorization argument for the surface: unlike a submitted workflow, an evaluation is not
/// caller-authored work, so what a caller can make this host do is bounded by what its operator chose
/// to publish.
/// </para>
/// <para>
/// <strong>Gated on a role, not merely on being authenticated.</strong> Every other route this host
/// serves runs the caller's <em>own</em> work — its bundle, its workflow, its tool call — under the
/// caller's own grant. This one spends the host's model budget on the operator's suites, and a
/// suite is hundreds of governed agent turns. That is a different kind of authority from "you hold a
/// valid token for this API", so it gets its own claim, following the same
/// <c>Harness.&lt;area&gt;.&lt;verb&gt;</c> convention as <c>Harness.Drift.Operate</c> and
/// <c>Harness.Learnings.Read</c>.
/// </para>
/// <para>
/// The role is granted to the synthetic principal in the anonymous development mode, so a local
/// developer is not locked out of a surface that has no other way to be exercised. That mode is an
/// explicit opt-in that already boots with a startup warning; it is not a way to reach this in a
/// deployment.
/// </para>
/// <para>
/// Ownership is never taken from the request. There is no owner field on the wire; the identity the
/// authenticated principal resolves to is what the run is stamped with and what every later read is
/// checked against. The role decides <em>whether you may evaluate at all</em>; ownership decides
/// <em>which runs are yours</em>. Holding the role does not let a caller read another's run.
/// </para>
/// </remarks>
[ApiController]
[Route("api/evals")]
[Authorize(Roles = ExecuteRole)]
[EnableRateLimiting(ExecutionApiServiceCollectionExtensions.DefaultRateLimitPolicy)]
public sealed class EvalsController : ControllerBase
{
    /// <summary>
    /// Role required to reach any evaluation endpoint.
    /// </summary>
    /// <remarks>
    /// One role for the whole surface rather than a read/execute split. The read endpoints only ever
    /// answer about the caller's <em>own</em> runs, so a reader who cannot start one has nothing to
    /// read — the split would grant access to an empty set. Contrast <c>DriftController</c>, whose
    /// read surface describes host-wide state and therefore genuinely separates from its operate role.
    /// </remarks>
    public const string ExecuteRole = "Harness.Evals.Execute";

    private readonly IMediator _mediator;
    private readonly IEvalDatasetCatalog _catalog;
    private readonly ICapabilityEnvelopeResolver _envelopeResolver;

    /// <summary>Initializes the controller.</summary>
    public EvalsController(
        IMediator mediator,
        IEvalDatasetCatalog catalog,
        ICapabilityEnvelopeResolver envelopeResolver)
    {
        ArgumentNullException.ThrowIfNull(mediator);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(envelopeResolver);

        _mediator = mediator;
        _catalog = catalog;
        _envelopeResolver = envelopeResolver;
    }

    /// <summary>Lists the dataset names this host will evaluate.</summary>
    /// <remarks>
    /// Answers an empty list rather than 404 when nothing is configured. "This host publishes no
    /// datasets" is a true and complete answer to the question asked, and a caller can act on it —
    /// whereas 404 on a route that exists would suggest the surface itself was absent.
    /// </remarks>
    [HttpGet("datasets")]
    [ProducesResponseType(typeof(EvalDatasetsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult ListDatasets() =>
        Ok(new EvalDatasetsResponse { Datasets = _catalog.ListNames() });

    /// <summary>
    /// Starts an evaluation run over the named datasets, returning a job identifier to poll.
    /// </summary>
    /// <remarks>
    /// Accepts and queues; it does not wait. A suite is hundreds of governed agent turns at the default
    /// ceilings, so the response says the work was taken, not that it finished — which is why it carries
    /// a status URL rather than a report.
    /// </remarks>
    /// <param name="request">The datasets to evaluate and how.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    [HttpPost("runs")]
    [ProducesResponseType(typeof(StartEvalRunResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> StartRun(
        [FromBody] StartEvalRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Resolved here, at the transport boundary, from the credential that invoked THIS request. Every
        // evaluation case is a governed agent turn that can invoke tools, so a run carries a grant for
        // the same reason a workflow run does — and it is the grant of whoever started it.
        var envelope = _envelopeResolver.Resolve(User);

        var result = await _mediator.Send(
            new StartEvalRunCommand
            {
                DatasetNames = request.Datasets,
                Options = new EvalRunOptions
                {
                    Repeats = request.Repeats,
                    Parallelism = request.Parallelism,
                    TagFilter = request.TagFilter,
                    FailRateThreshold = request.FailRateThreshold
                },
                OwnerId = User.GetUserId(),
                TenantId = User.GetTenantId(),
                Envelope = envelope
            },
            cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess || result.Value is null)
            return MapFailure(result);

        var statusUrl = $"/api/evals/runs/{result.Value.JobId}";
        return Accepted(statusUrl, new StartEvalRunResponse
        {
            JobId = result.Value.JobId,
            StatusUrl = statusUrl
        });
    }

    /// <summary>Reads the state of an evaluation run the caller started, and its report once finished.</summary>
    /// <param name="jobId">The run to read.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    [HttpGet("runs/{jobId}")]
    [ProducesResponseType(typeof(EvalRunResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRun(string jobId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(
            new GetEvalRunQuery
            {
                JobId = jobId,
                OwnerId = User.GetUserId(),
                TenantId = User.GetTenantId()
            },
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess && result.Value is not null
            ? Ok(EvalRunResponse.FromView(result.Value))
            : MapFailure(result);
    }

    /// <summary>Stops an evaluation run the caller started, if it has not begun executing.</summary>
    /// <remarks>
    /// <para>
    /// Answers 200 with <c>stopped: false</c> when the run was already executing. Unlike a workflow,
    /// an evaluation in flight cannot be signalled to stop — it is a suite of agent turns with no
    /// cancellation registry behind it — so this reports the truth rather than claiming an interruption
    /// that will not happen. What bounds a runaway suite is the per-run execution ceiling, applied
    /// before any case runs.
    /// </para>
    /// <para>
    /// Answers 409 for a run that has already finished: there is nothing to cancel, and reporting
    /// success would suggest this call changed something.
    /// </para>
    /// </remarks>
    /// <param name="jobId">The run to stop.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    [HttpDelete("runs/{jobId}")]
    [ProducesResponseType(typeof(CancelEvalRunResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CancelRun(string jobId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(
            new CancelEvalRunCommand
            {
                JobId = jobId,
                OwnerId = User.GetUserId(),
                TenantId = User.GetTenantId()
            },
            cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess || result.Value is null)
            return MapFailure(result);

        return Ok(new CancelEvalRunResponse { JobId = jobId, Stopped = result.Value.Stopped });
    }

    private IActionResult MapFailure(Result result) =>
        this.FailureResponse(result, "Evaluation operation failed");
}
