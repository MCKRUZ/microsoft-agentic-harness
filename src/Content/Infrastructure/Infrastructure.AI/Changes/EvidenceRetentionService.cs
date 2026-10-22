using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using Domain.Common.Config;
using Infrastructure.AI.BackgroundServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Changes;

/// <summary>
/// Deletes change-gate evidence nothing has stored for <c>AppConfig:AI:Changes:EvidenceRetention:RetainFor</c>, unless an
/// open proposal points at it (#901).
/// </summary>
/// <remarks>
/// <para>
/// Fails closed: the evidence open proposals point at is read from the proposal store one status at a time, and if any
/// listing comes back full, so that some open proposal may be missing from it, nothing is deleted that sweep.
/// </para>
/// <para>
/// Both stores are resolved when the sweep runs. Only the shipped <see cref="FileSystemEvidenceStore"/> is swept; a
/// replacement governs its own retention.
/// </para>
/// </remarks>
internal sealed class EvidenceRetentionService : PeriodicSweepService
{
    /// <summary>The most blobs one batch removes; a sweep runs further batches while each comes back full.</summary>
    internal const int MaxBlobsPerBatch = 1000;

    /// <summary>
    /// The most open proposals of one status the sweep reads. A listing this long may have been cut short, so it stops
    /// the sweep rather than risk deleting evidence an unseen proposal points at.
    /// </summary>
    internal const int MaxOpenProposalsPerStatus = 10_000;

    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceProvider _services;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EvidenceRetentionService> _logger;

    /// <summary>Initializes a new <see cref="EvidenceRetentionService"/>.</summary>
    /// <param name="services">Resolves the evidence and proposal stores when the sweep runs.</param>
    /// <param name="config">Supplies the period and interval, read live on every tick.</param>
    /// <param name="timeProvider">Drives the wait and the cutoff.</param>
    /// <param name="logger">Receives the per-sweep result, a sweep held back, and any failure.</param>
    public EvidenceRetentionService(
        IServiceProvider services,
        IOptionsMonitor<AppConfig> config,
        TimeProvider timeProvider,
        ILogger<EvidenceRetentionService> logger)
        : base(MinInterval, timeProvider, logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        _services = services;
        _config = config;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override SweepRetentionSnapshot ReadRetention()
    {
        var retention = _config.CurrentValue.AI.Changes.EvidenceRetention;
        return new SweepRetentionSnapshot(retention.Enabled, retention.SweepInterval, retention.RetainFor);
    }

    /// <inheritdoc />
    /// <remarks>A period under a day is refused here as well as at startup, because a reloaded value is not re-validated.</remarks>
    protected override async Task<int> SweepAsync(TimeSpan gracePeriod, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(gracePeriod, RetentionPeriodConfig.MinimumRetainFor);
        if (_services.GetService<IEvidenceStore>() is not FileSystemEvidenceStore evidence)
            return 0;

        var inUse = await EvidenceOpenProposalsPointAtAsync(cancellationToken);
        if (inUse is null)
            return 0;

        var cutoff = _timeProvider.GetUtcNow() - gracePeriod;
        return await SweepBatches.RunAsync(
            () => evidence.DeleteNotStoredSinceAsync(cutoff, inUse, MaxBlobsPerBatch, cancellationToken),
            MaxBlobsPerBatch);
    }

    /// <summary>
    /// The hashes every open proposal's gate decisions point at, or <see langword="null"/> when that cannot be known in
    /// full: no proposal store, or a listing that may have been cut short.
    /// </summary>
    private async Task<IReadOnlySet<string>?> EvidenceOpenProposalsPointAtAsync(CancellationToken cancellationToken)
    {
        if (_services.GetService<IChangeProposalStore>() is not { } proposals)
            return null;

        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var status in Enum.GetValues<ChangeProposalStatus>().Where(s => !ChangeProposalStateTransitions.IsTerminal(s)))
        {
            var open = await proposals.ListAsync(
                new ChangeProposalQuery { Status = status, MaxResults = MaxOpenProposalsPerStatus }, cancellationToken);
            if (open.Count >= MaxOpenProposalsPerStatus)
            {
                _logger.LogWarning(
                    "Evidence retention skipped: {Count} or more open proposals are {Status}, so the evidence they point at cannot be listed in full.",
                    MaxOpenProposalsPerStatus, status);
                return null;
            }

            foreach (var proposal in open)
                hashes.UnionWith(proposal.History.Select(d => d.EvidenceHash).OfType<string>());
        }

        return hashes;
    }

    /// <inheritdoc />
    protected override string ReclaimedLogMessage => "Evidence retention deleted {Count} evidence blob(s) past the retention period.";

    /// <inheritdoc />
    protected override string FailureLogMessage => "Evidence retention sweep failed; will retry on the next interval.";
}
