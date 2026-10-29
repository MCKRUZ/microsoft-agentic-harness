using Application.AI.Common.Interfaces.RAG;
using Application.Core.Compliance;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Config;
using Infrastructure.AI.BackgroundServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Documents;

/// <summary>
/// The optional document age limit (#901): removes each document not loaded for
/// <c>AppConfig:AI:Rag:Documents:Retention:RetainFor</c>, with everything drawn from it. Off by default.
/// </summary>
/// <remarks>
/// <para>
/// Each removal goes through <see cref="AuditedDocumentRemoval"/>, the path an operator's removal takes, so it reaches every
/// store and leaves the same audit records, with <see cref="ErasureActor.Retention"/> as the actor.
/// </para>
/// <para>
/// A document is checked again just before it is removed, so one loaded again since the list was read is kept. A removal
/// that ends <c>Partial</c> keeps its load record and is tried again on the next sweep; a retry is recorded in the
/// compliance audit trail only when it ends differently from the last recorded attempt (#998,
/// <see cref="DocumentRemovalRetries"/>), so the trail grows with requests rather than with sweeps. At most
/// <see cref="MaxDocumentsPerSweep"/> documents are removed per sweep, oldest first, so switching the limit on over a large
/// corpus spreads the work over several sweeps.
/// </para>
/// <para>
/// It refuses to run where <see cref="DocumentRetentionSafety"/> says it may not: at construction, which stops the host
/// starting, and again on every sweep, because the setting can be switched on without a restart.
/// </para>
/// </remarks>
internal sealed class DocumentRetentionService : PeriodicSweepService
{
    /// <summary>The most documents one sweep removes.</summary>
    internal const int MaxDocumentsPerSweep = 200;

    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly IDocumentLoadLedger _ledger;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly TimeProvider _timeProvider;

    /// <summary>What was last recorded for each document still being retried; lives as long as the service.</summary>
    private readonly DocumentRemovalRetries _retries = new();

    /// <summary>Initializes a new <see cref="DocumentRetentionService"/>.</summary>
    /// <param name="scopes">Opens the scope each sweep's removals run in.</param>
    /// <param name="ledger">When each document was last loaded.</param>
    /// <param name="config">Supplies the age limit, read live on every tick.</param>
    /// <param name="timeProvider">Drives the wait and the cutoff.</param>
    /// <param name="logger">Receives the per-sweep result and any failure.</param>
    /// <exception cref="InvalidOperationException">The age limit is on where <see cref="DocumentRetentionSafety"/> forbids it.</exception>
    public DocumentRetentionService(
        IServiceScopeFactory scopes,
        IDocumentLoadLedger ledger,
        IOptionsMonitor<AppConfig> config,
        TimeProvider timeProvider,
        ILogger<DocumentRetentionService> logger)
        : base(MinInterval, timeProvider, logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(config);

        _scopes = scopes;
        _ledger = ledger;
        _config = config;
        _timeProvider = timeProvider;

        if (DocumentRetentionSafety.Refusal(config.CurrentValue, ledger) is { } refusal)
            throw new InvalidOperationException(refusal);
    }

    /// <inheritdoc />
    protected override SweepRetentionSnapshot ReadRetention()
    {
        var retention = _config.CurrentValue.AI.Rag.Documents.Retention;
        return new SweepRetentionSnapshot(retention.Enabled, retention.SweepInterval, retention.RetainFor);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A period under a day, and a configuration <see cref="DocumentRetentionSafety"/> forbids, are refused here as well as
    /// at startup, because a reloaded value is not re-validated.
    /// </remarks>
    protected override async Task<int> SweepAsync(TimeSpan gracePeriod, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(gracePeriod, RetentionPeriodConfig.MinimumRetainFor);
        if (DocumentRetentionSafety.Refusal(_config.CurrentValue, _ledger) is { } refusal)
            throw new InvalidOperationException(refusal);

        var cutoff = _timeProvider.GetUtcNow() - gracePeriod;
        var expired = await _ledger.ListLoadedBeforeAsync(cutoff, MaxDocumentsPerSweep, cancellationToken);
        _retries.KeepOnly(expired); // A document no longer listed was removed, or loaded again.
        if (expired.Count == 0)
            return 0;

        await using var scope = _scopes.CreateAsyncScope();
        var removal = scope.ServiceProvider.GetRequiredService<AuditedDocumentRemoval>();

        var removed = 0;
        foreach (var documentId in expired)
        {
            if (await _ledger.GetLastLoadedAsync(documentId, cancellationToken) is not { } lastLoaded || lastLoaded >= cutoff)
                continue;

            // The load judged is the bound: a load recorded after it keeps the record.
            var result = await removal.RemoveAsync(documentId, ErasureActor.Retention, cancellationToken, lastLoaded, _retries);
            if (result.IsSuccess && result.Value!.Completeness == ErasureCompleteness.Full)
                removed++;
        }

        return removed;
    }

    /// <inheritdoc />
    protected override string ReclaimedLogMessage =>
        "Document age limit removed {Count} document(s) not loaded for the retention period.";

    /// <inheritdoc />
    protected override string FailureLogMessage => "Document age limit sweep failed; will retry on the next interval.";
}
