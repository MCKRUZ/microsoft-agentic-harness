using Application.AI.Common.Interfaces.Bundles;
using Domain.AI.Bundles;
using Domain.Common;
using Domain.Common.Config;
using MediatR;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.CQRS.Bundles.GetBundleRun;

/// <summary>
/// Handles <see cref="GetBundleRunQuery"/>: returns the current run record when it exists under the given
/// handle, or a not-found result when the job id is unknown, already swept, or belongs to a different handle.
/// </summary>
/// <remarks>
/// A run whose handle does not match the query's handle is reported as not found (never as
/// "belongs to another handle"), so the poll surface leaks nothing about runs the caller does not own.
/// </remarks>
public sealed class GetBundleRunQueryHandler
    : IRequestHandler<GetBundleRunQuery, Result<BundleRunRecord>>
{
    private readonly IBundleRunJobStore _jobStore;
    private readonly IOptionsMonitor<AppConfig> _config;

    /// <summary>Initializes a new <see cref="GetBundleRunQueryHandler"/>.</summary>
    public GetBundleRunQueryHandler(IBundleRunJobStore jobStore, IOptionsMonitor<AppConfig> config)
    {
        ArgumentNullException.ThrowIfNull(jobStore);
        ArgumentNullException.ThrowIfNull(config);

        _jobStore = jobStore;
        _config = config;
    }

    /// <inheritdoc />
    public Task<Result<BundleRunRecord>> Handle(GetBundleRunQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_config.CurrentValue.AI.BundleExecution.Enabled)
        {
            return Task.FromResult(Result<BundleRunRecord>.Forbidden(
                "Bundle execution is disabled. Set AppConfig.AI.BundleExecution.Enabled = true to enable it."));
        }

        var record = _jobStore.Get(request.JobId);
        if (record is null || !string.Equals(record.Handle, request.Handle, StringComparison.Ordinal))
        {
            return Task.FromResult(Result<BundleRunRecord>.NotFound(
                "Bundle run not found. It may never have existed, expired, or belongs to a different handle."));
        }

        return Task.FromResult(Result<BundleRunRecord>.Success(record));
    }
}
