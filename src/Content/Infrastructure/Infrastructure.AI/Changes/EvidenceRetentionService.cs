using Application.AI.Common.Interfaces.Changes;
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
/// Fails closed: the evidence open proposals point at comes from
/// <see cref="IChangeProposalStore.GetEvidenceHashesOfOpenProposalsAsync"/>, which reads every open proposal, quarantined
/// ones included, and throws rather than leave one out. With no proposal store, or when that throws, nothing is deleted.
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

    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceProvider _services;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new <see cref="EvidenceRetentionService"/>.</summary>
    /// <param name="services">Resolves the evidence and proposal stores when the sweep runs.</param>
    /// <param name="config">Supplies the period and interval, read live on every tick.</param>
    /// <param name="timeProvider">Drives the wait and the cutoff.</param>
    /// <param name="logger">Receives the per-sweep result and any failure.</param>
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
        if (_services.GetService<IEvidenceStore>() is not FileSystemEvidenceStore evidence
            || _services.GetService<IChangeProposalStore>() is not { } proposals)
            return 0;

        var inUse = await proposals.GetEvidenceHashesOfOpenProposalsAsync(cancellationToken);

        var cutoff = _timeProvider.GetUtcNow() - gracePeriod;
        return await SweepBatches.RunAsync(
            () => evidence.DeleteNotStoredSinceAsync(cutoff, inUse, MaxBlobsPerBatch, cancellationToken),
            MaxBlobsPerBatch);
    }

    /// <inheritdoc />
    protected override string ReclaimedLogMessage => "Evidence retention deleted {Count} evidence blob(s) past the retention period.";

    /// <inheritdoc />
    protected override string FailureLogMessage => "Evidence retention sweep failed; will retry on the next interval.";
}
