using System.Globalization;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.Common.Helpers;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.KnowledgeGraph.Compliance;

/// <summary>
/// Background service that removes knowledge-graph nodes whose retention period has passed (#901).
/// </summary>
/// <remarks>
/// <para>
/// <strong>What expires.</strong> A node expires when its stamped <see cref="GraphNode.ExpiresAt"/> has passed (types
/// listed in <c>RetentionPolicies</c>), or when it is personal knowledge (remembered facts, work episodes and episodic
/// segments; see <c>IsPersonalKnowledge</c>) last written longer ago than <c>PersonalKnowledgeRetention</c>. The second
/// rule is computed here from the time every write stamps, or a node's creation when it predates that, so it also
/// reaches nodes written before it existed. A node with neither is never treated as old.
/// </para>
/// <para>
/// <strong>Nodes only, never their documents.</strong> Expired nodes go through
/// <see cref="IErasureOrchestrator.ExpireNodesAsync"/>, which removes them, their edges and feedback weights but not the
/// document content behind their chunk references: that document is still the corpus for every other node extracted
/// from it. Before #901 this used erasure, which deleted the whole source document from both indexes when one node aged.
/// </para>
/// <para>
/// <strong>Bounded.</strong> At most <see cref="BatchSize"/> nodes per run, so the first run after an upgrade, which may
/// find years of old personal knowledge, does not hold the stores for long; the rest go on later runs.
/// </para>
/// <para>
/// The interval is read live, after a 30-second start delay, with a one-minute floor; zero switches the run off until the
/// setting changes. An unreadable reload keeps the last good interval.
/// </para>
/// </remarks>
public sealed class RetentionEnforcementService : BackgroundService
{
    /// <summary>The most nodes one run removes.</summary>
    public const int BatchSize = 1000;

    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan OffInterval = TimeSpan.FromHours(1);
    private const string RememberedIdPrefix = "memory:";
    private static readonly HashSet<string> PersonalTypes = new(StringComparer.Ordinal) { "WorkEpisode", "EpisodicSegment" };

    private readonly IKnowledgeGraphStore _graphStore;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RetentionEnforcementService> _logger;

    /// <summary>Initializes a new instance of the <see cref="RetentionEnforcementService"/> class.</summary>
    /// <param name="graphStore">The graph store to scan for expired nodes.</param>
    /// <param name="scopeFactory">
    /// Creates a DI scope per run to resolve the scoped <see cref="IErasureOrchestrator"/>; this singleton must not
    /// capture it directly.
    /// </param>
    /// <param name="config">Supplies the interval and periods, read live.</param>
    /// <param name="timeProvider">Drives the wait and decides what has expired.</param>
    /// <param name="logger">Logger for recording enforcement activity.</param>
    public RetentionEnforcementService(
        IKnowledgeGraphStore graphStore,
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<AppConfig> config,
        TimeProvider timeProvider,
        ILogger<RetentionEnforcementService> logger)
    {
        ArgumentNullException.ThrowIfNull(graphStore);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _graphStore = graphStore;
        _scopeFactory = scopeFactory;
        _config = config;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = new LastKnownGoodSetting<TimeSpan>(
            () => _config.CurrentValue.AI.Rag.GraphRag.RetentionEnforcementInterval, TimeSpan.Zero, _logger,
            "graph retention interval");

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), _timeProvider, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                var current = interval.Current;
                if (current > TimeSpan.Zero)
                    await RunOnceAsync(stoppingToken);

                var wait = current <= TimeSpan.Zero ? OffInterval : current < MinInterval ? MinInterval : current;
                await Task.Delay(wait, _timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting down.
        }
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await EnforceRetentionAsync(_timeProvider.GetUtcNow(), stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Retention enforcement failed; will retry on the next interval.");
        }
    }

    /// <summary>
    /// Removes up to <see cref="BatchSize"/> nodes that have expired as of <paramref name="now"/>. Public for testability
    /// without a hosted-service lifecycle.
    /// </summary>
    /// <param name="now">The instant expiry is judged against.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many nodes were removed.</returns>
    public async Task<int> EnforceRetentionAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var personalCutoff = PersonalCutoff(_config.CurrentValue.AI.Rag.GraphRag.PersonalKnowledgeRetention, now);
        var listedTypes = _config.CurrentValue.AI.Rag.GraphRag.RetentionPolicies.Keys.ToHashSet(StringComparer.Ordinal);

        var expired = (await _graphStore.GetAllNodesAsync(cancellationToken))
            .Where(n => HasExpired(n, now, personalCutoff, listedTypes))
            .Take(BatchSize)
            .ToList();

        if (expired.Count == 0)
        {
            _logger.LogDebug("Retention enforcement: no expired nodes found");
            return 0;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IErasureOrchestrator>();
        // The node instances already scanned, not their ids: the compliance-aware store returns null for an expired
        // node on re-fetch.
        var receipt = await orchestrator.ExpireNodesAsync(expired, cancellationToken);
        _logger.LogInformation("Retention enforcement: removed {Nodes} expired nodes", receipt.NodesDeleted);
        return receipt.NodesDeleted;
    }

    /// <summary>
    /// The instant personal knowledge must have been written after, or <see langword="null"/> when the personal rule does
    /// not run: switched off (zero), or below the one-day floor. A reloaded value is not re-validated, so a mistyped
    /// period (minutes meant as days) must not delete everyone's memories; it is refused here and logged. A period too
    /// long to subtract from now keeps everything rather than aborting the run.
    /// </summary>
    private DateTimeOffset? PersonalCutoff(TimeSpan personal, DateTimeOffset now)
    {
        if (personal == TimeSpan.Zero)
            return null;

        if (personal < RetentionPeriodConfig.MinimumRetainFor)
        {
            _logger.LogError(
                "PersonalKnowledgeRetention {Period} is under the one-day minimum; personal knowledge is not expired until it is corrected.",
                personal);
            return null;
        }

        return personal >= now - DateTimeOffset.MinValue ? DateTimeOffset.MinValue : now - personal;
    }

    /// <summary>
    /// Whether a node's stamped expiry has passed, or it is personal knowledge last written before
    /// <paramref name="personalCutoff"/> (<see langword="null"/> when the personal rule is off).
    /// </summary>
    private static bool HasExpired(GraphNode node, DateTimeOffset now, DateTimeOffset? personalCutoff, HashSet<string> listedTypes)
    {
        if (node.ExpiresAt is { } expiresAt)
            return expiresAt < now;

        return personalCutoff is { } cutoff
            && IsPersonalKnowledge(node, listedTypes)
            && LastWritten(node) is { } written
            && written < cutoff;
    }

    /// <summary>
    /// When the node was last written: the time every write stamps, or for a node written before that existed, its
    /// creation. <see langword="null"/> when neither is known, and then it never counts as old.
    /// </summary>
    private static DateTimeOffset? LastWritten(GraphNode node) =>
        node.Properties.TryGetValue(ComplianceAwareGraphStore.LastWrittenAtProperty, out var text)
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var written)
            ? written
            : node.CreatedAt;

    /// <summary>
    /// Knowledge held about one person, named rather than inferred: what <c>KnowledgeMemoryService</c> remembered for
    /// someone (ids <c>memory:{tenant}:{user}:{key}</c>, including skill-training experience it stores), their work
    /// episodes and their episodic conversation segments. Each also needs an owner and no chunk references. Other owned
    /// nodes are not about a person (a drift event's owner is an agent or skill scope), and a type with a listed policy
    /// is governed by that policy.
    /// </summary>
    private static bool IsPersonalKnowledge(GraphNode node, HashSet<string> listedTypes) =>
        !string.IsNullOrEmpty(node.OwnerId)
        && node.ChunkIds.Count == 0
        && !listedTypes.Contains(node.Type)
        && (node.Id.StartsWith(RememberedIdPrefix, StringComparison.Ordinal) || PersonalTypes.Contains(node.Type));
}
