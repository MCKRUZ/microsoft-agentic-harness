using Application.AI.Common.Interfaces.Evaluation;
using Application.AI.Common.Interfaces.Runs;
using Domain.AI.Runs;
using Domain.Common;
using Domain.Common.Config;
using MediatR;
using Microsoft.Extensions.Options;

namespace Application.Core.CQRS.Evaluation.Runs;

/// <summary>
/// Handles <see cref="GetEvalRunQuery"/>: reads an evaluation run the caller started.
/// </summary>
/// <remarks>
/// <para>
/// Answers NotFound for a run belonging to someone else, a run that never existed, and a run of another
/// kind alike. A caller learning "that exists but is not yours" could enumerate other callers' work by
/// guessing identifiers, so the three are deliberately one answer.
/// </para>
/// <para>
/// <strong>The kind is checked, not assumed.</strong> Job ids are minted from one sequence for every
/// kind of run, so without this a caller could read a workflow run through the evaluation route. It
/// would disclose little on its own — the projection carries no envelope — but it would confirm that a
/// job id it holds belongs to a workflow, and the same omission on the cancel path would let it stop
/// one.
/// </para>
/// </remarks>
public sealed class GetEvalRunQueryHandler : IRequestHandler<GetEvalRunQuery, Result<EvalRunView>>
{
    private readonly IRunJobStore _runStore;
    private readonly IEvalRunSubmissionStore _submissions;
    private readonly IOptionsMonitor<AppConfig> _config;

    /// <summary>Initializes a new <see cref="GetEvalRunQueryHandler"/>.</summary>
    public GetEvalRunQueryHandler(
        IRunJobStore runStore,
        IEvalRunSubmissionStore submissions,
        IOptionsMonitor<AppConfig> config)
    {
        ArgumentNullException.ThrowIfNull(runStore);
        ArgumentNullException.ThrowIfNull(submissions);
        ArgumentNullException.ThrowIfNull(config);

        _runStore = runStore;
        _submissions = submissions;
        _config = config;
    }

    /// <inheritdoc />
    public Task<Result<EvalRunView>> Handle(GetEvalRunQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_config.CurrentValue.AI.Evaluation.Enabled)
        {
            return Task.FromResult(Result<EvalRunView>.Forbidden(
                "Evaluation is disabled. Set AppConfig.AI.Evaluation.Enabled = true to enable it."));
        }

        // Scoped by owner and tenant inside the store, which is what makes the submission read below
        // safe: that store applies no scope of its own and must never be reached without this first.
        var record = _runStore.Get(request.JobId, request.OwnerId, request.TenantId);

        if (record is null || record.Kind != RunKind.Evaluation)
            return Task.FromResult(Result<EvalRunView>.NotFound($"No run {request.JobId} found."));

        // Absent is normal, not an error: the record and the submission are dropped by the same sweep,
        // but nothing makes the two writes atomic. A caller polling in that window is owed the run's
        // status rather than a failure — the run genuinely happened, and its lifecycle is the part that
        // still answers.
        var submission = _submissions.Get(record.JobId);

        return Task.FromResult(Result<EvalRunView>.Success(new EvalRunView
        {
            Run = record,
            DatasetNames = submission?.DatasetNames ?? [],
            Report = submission?.Report
        }));
    }
}
