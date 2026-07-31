using Application.AI.Common.CQRS.Evaluation.IngestEvalRun;
using Application.AI.Common.Interfaces.Evaluation;
using Application.AI.Common.Interfaces.Runs;
using Application.AI.Common.Services.Governance;
using Application.Core.CQRS.Evaluation.RunEvalSuite;
using Domain.AI.Evaluation;
using Domain.AI.Runs;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Runs;

/// <summary>
/// Runs a submitted evaluation suite for the shared run substrate, delegating to
/// <c>RunEvalSuiteCommand</c>.
/// </summary>
/// <remarks>
/// <para>
/// Thin on purpose, exactly as <see cref="WorkflowRunKindExecutor"/> is. Everything that makes an eval
/// run safe — dataset confinement, the per-run cost ceilings, the size cap, loader dispatch — already
/// lives behind that command and applies to <em>every</em> caller of it, not only this one. What this
/// adds is the two things only the run path knows: turning the caller's dataset names back into paths,
/// and turning a report into an outcome.
/// </para>
/// <para>
/// <strong>Names become paths here, at the last possible moment.</strong> The submission holds names
/// because a path must never cross the trust boundary, and this is the first point at which a path is
/// unavoidable — the command reads files. Resolution goes through <see cref="IEvalDatasetCatalog"/>,
/// which produces only paths it found by enumerating a configured root, and the command's own guard
/// then confines whatever it is handed. A name that no longer resolves fails the run rather than being
/// skipped: a suite silently evaluated without one of its datasets reports a pass rate for something
/// that never ran.
/// </para>
/// </remarks>
public sealed class EvalRunKindExecutor : IRunKindExecutor
{
    private readonly IMediator _mediator;
    private readonly IEvalDatasetCatalog _catalog;
    private readonly IEvalRunSubmissionStore _submissions;
    private readonly ILogger<EvalRunKindExecutor> _logger;

    /// <summary>Initializes the executor.</summary>
    public EvalRunKindExecutor(
        IMediator mediator,
        IEvalDatasetCatalog catalog,
        IEvalRunSubmissionStore submissions,
        ILogger<EvalRunKindExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(mediator);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(submissions);
        ArgumentNullException.ThrowIfNull(logger);

        _mediator = mediator;
        _catalog = catalog;
        _submissions = submissions;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<RunCompletion>> ExecuteAsync(RunRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        var submission = _submissions.Get(record.JobId);
        if (submission is null)
        {
            // Unreachable on the accepted path — the submission is stored before the run is queued, and
            // it is only reclaimed once the record is, by which point nothing dispatches. Answered
            // rather than thrown so a missing entry fails this one run instead of surfacing as an
            // unexpected dispatcher exception.
            _logger.LogError("Run {JobId} has no stored evaluation submission.", record.JobId);
            return Result<RunCompletion>.Fail("The run's evaluation request could not be read.");
        }

        var resolution = _catalog.Resolve(submission.DatasetNames);
        if (!resolution.IsComplete)
        {
            // The name resolved when the run was accepted and does not now — the file was removed or a
            // root was reconfigured in between. Failing the whole run rather than skipping the dataset:
            // a suite quietly evaluated without one of its parts reports a pass rate for something that
            // never ran.
            _logger.LogWarning(
                "Run {JobId} names dataset {Dataset}, which no longer resolves.",
                record.JobId, resolution.MissingName);

            return Result<RunCompletion>.Fail($"Dataset '{resolution.MissingName}' is no longer available.");
        }

        // The caller's grant is armed for the whole evaluation, exactly as PlanRunExecutor arms it for
        // a workflow. An evaluation is not a passive read: every case is a governed agent turn that
        // can invoke tools, so without this the suite would run outside the envelope the caller was
        // resolved to hold — a caller could reach, through an eval case, tools it is denied directly.
        // Ambient (AsyncLocal), so it flows into the per-case scopes the agent invoker creates.
        //
        // Identity is not armed here: AgentContextPropagationBehavior initializes the execution
        // context per agent turn, inside those same scopes. That ordering matters — the governor fails
        // closed on identity-less enveloped tool calls — and it holds because the behaviour runs on
        // every turn dispatched below this point.
        using var granted = CapabilityEnvelopeAccessor.Begin(record.Envelope);

        var outcome = await _mediator.Send(
            new RunEvalSuiteCommand
            {
                DatasetPaths = resolution.Paths,
                Options = submission.Options
            },
            cancellationToken).ConfigureAwait(false);

        if (!outcome.IsSuccess || outcome.Value is null)
        {
            // The command's failures are already caller-safe — refusals name a ceiling or a dataset,
            // never a path or exception text — so they pass through rather than being flattened into
            // "it failed". A caller learns why its own run did not work.
            return Result<RunCompletion>.Fail(
                outcome.Errors.Count > 0 ? [.. outcome.Errors] : ["The evaluation produced no report."]);
        }

        var report = outcome.Value;

        if (!_submissions.AttachReport(record.JobId, report))
        {
            // Logged, not failed. The evaluation ran and cost what it cost; reporting the run as failed
            // because the result could not be filed would be untrue about the work and would not give
            // the spend back. The caller sees a succeeded run with no report, which is the honest
            // description of what happened.
            _logger.LogWarning(
                "Run {JobId} finished but its submission was gone, so its report could not be attached.",
                record.JobId);
        }

        await IngestAsync(record.JobId, report, cancellationToken).ConfigureAwait(false);

        // Succeeded means the suite ran, not that it passed. A run whose cases mostly failed is a
        // completed evaluation with a Fail verdict in its report, and collapsing the two would leave a
        // caller unable to tell a failing suite from a broken host — the first is the answer they asked
        // for, the second means they never got one.
        _logger.LogInformation(
            "Run {JobId} evaluated {Cases} case(s) with verdict {Verdict}.",
            record.JobId,
            report.Results.Count,
            report.OverallVerdict);

        return Result<RunCompletion>.Success(RunCompletion.Succeeded());
    }

    /// <summary>
    /// Files the report into the durable eval store the dashboard reads, in-process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what a server-side run buys over the CLI: the CLI produces a report and then posts it
    /// back over HTTP to be ingested, and a run that already executes inside the host has no reason to
    /// leave the process to file its own result. Without this the report lives only in the submission
    /// store and disappears with it at <c>RunRecordTtl</c> — an evaluation whose history no dashboard
    /// ever sees, which is most of the reason to keep evaluations at all.
    /// </para>
    /// <para>
    /// <strong>A failed ingest does not fail the run.</strong> The evaluation ran, the spend is already
    /// incurred, and the caller can still read the report from its own run. Marking the run failed
    /// would misreport the work, and would invite a retry that pays for the whole suite again to fix a
    /// bookkeeping problem. It is logged at warning because a silently unrecorded run is exactly the
    /// kind of gap that is invisible until someone asks why the dashboard is missing a week.
    /// </para>
    /// <para>
    /// Ingest is idempotent on <c>RunId</c>, so a re-dispatched run cannot double-count.
    /// </para>
    /// </remarks>
    private async Task IngestAsync(string jobId, EvalRunReport report, CancellationToken cancellationToken)
    {
        try
        {
            var ingested = await _mediator
                .Send(new IngestEvalRunCommand { Report = report }, cancellationToken)
                .ConfigureAwait(false);

            if (!ingested.IsSuccess)
            {
                _logger.LogWarning(
                    "Run {JobId} produced report {RunId} but it could not be recorded for the "
                    + "dashboard: {Errors}",
                    jobId, report.RunId, string.Join("; ", ingested.Errors));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A host that has not registered the eval store at all reaches here rather than taking the
            // run down with it. The evaluation still succeeded.
            _logger.LogWarning(
                ex, "Run {JobId} produced report {RunId} but ingest threw.", jobId, report.RunId);
        }
    }
}
