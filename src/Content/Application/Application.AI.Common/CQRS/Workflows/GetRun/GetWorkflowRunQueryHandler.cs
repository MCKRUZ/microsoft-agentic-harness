using Application.AI.Common.Interfaces.Runs;
using Domain.AI.Runs;
using Domain.Common;
using Domain.Common.Config;
using MediatR;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.CQRS.Workflows.GetRun;

/// <summary>
/// Handles <see cref="GetWorkflowRunQuery"/>: reads a run the caller started.
/// </summary>
/// <remarks>
/// Both the owner check and the workflow check answer NotFound rather than Forbidden. A caller
/// learning "that run exists but is not yours" would be able to enumerate other callers' work by
/// guessing identifiers, so a run belonging to someone else and a run that never existed are
/// deliberately the same answer. The workflow is checked too, so a valid job id cannot be read
/// through the wrong workflow's route and imply a relationship that does not exist.
/// </remarks>
public sealed class GetWorkflowRunQueryHandler : IRequestHandler<GetWorkflowRunQuery, Result<RunRecord>>
{
    private readonly IRunJobStore _runStore;
    private readonly IOptionsMonitor<AppConfig> _config;

    /// <summary>Initializes a new <see cref="GetWorkflowRunQueryHandler"/>.</summary>
    public GetWorkflowRunQueryHandler(IRunJobStore runStore, IOptionsMonitor<AppConfig> config)
    {
        ArgumentNullException.ThrowIfNull(runStore);
        ArgumentNullException.ThrowIfNull(config);

        _runStore = runStore;
        _config = config;
    }

    /// <inheritdoc />
    public Task<Result<RunRecord>> Handle(GetWorkflowRunQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_config.CurrentValue.AI.WorkflowSubmission.Enabled)
        {
            return Task.FromResult(Result<RunRecord>.Forbidden(
                "Workflow submission is disabled. Set AppConfig.AI.WorkflowSubmission.Enabled = true to enable it."));
        }

        var record = _runStore.Get(request.JobId, request.OwnerId, request.TenantId);

        // A run under a different workflow is reported as missing rather than returned: the route
        // states a relationship, and answering it with a record that contradicts the route would let a
        // caller confirm which workflow a job belongs to by trying routes until one succeeded.
        if (record is null
            || record.Kind != RunKind.Workflow
            || !string.Equals(record.TargetId, request.WorkflowId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(Result<RunRecord>.NotFound($"No run {request.JobId} found."));
        }

        return Task.FromResult(Result<RunRecord>.Success(record));
    }
}
