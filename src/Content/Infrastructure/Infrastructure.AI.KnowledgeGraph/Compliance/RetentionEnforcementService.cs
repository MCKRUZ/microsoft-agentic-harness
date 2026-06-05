using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.KnowledgeGraph.Compliance;

/// <summary>
/// Background service that periodically purges expired knowledge graph nodes.
/// Queries all nodes via <see cref="IKnowledgeGraphStore.GetAllNodesAsync"/>,
/// filters by <see cref="GraphNode.ExpiresAt"/>, and delegates deletion to
/// <see cref="IErasureOrchestrator"/> for cascading cleanup across graph,
/// feedback, and vector stores.
/// </summary>
/// <remarks>
/// Runs on a 24-hour cycle with a 30-second startup delay to allow the host
/// to fully initialize. Production deployments should tune the interval via
/// configuration and consider using indexed queries in the graph store
/// implementation rather than scanning all nodes.
/// </remarks>
public sealed class RetentionEnforcementService : BackgroundService
{
    private readonly IKnowledgeGraphStore _graphStore;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RetentionEnforcementService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RetentionEnforcementService"/> class.
    /// </summary>
    /// <param name="graphStore">The graph store to scan for expired nodes.</param>
    /// <param name="scopeFactory">
    /// Creates a DI scope per enforcement run to resolve the scoped <see cref="IErasureOrchestrator"/>.
    /// This service is a singleton hosted service, so it must not capture a scoped dependency directly.
    /// </param>
    /// <param name="logger">Logger for recording enforcement activity.</param>
    public RetentionEnforcementService(
        IKnowledgeGraphStore graphStore,
        IServiceScopeFactory scopeFactory,
        ILogger<RetentionEnforcementService> logger)
    {
        ArgumentNullException.ThrowIfNull(graphStore);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _graphStore = graphStore;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnforceRetentionAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Retention enforcement failed");
            }

            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }

    /// <summary>
    /// Scans all nodes for expired entries and delegates deletion to the erasure
    /// orchestrator. Public for testability without requiring a hosted service lifecycle.
    /// </summary>
    /// <param name="now">The current timestamp to compare against <see cref="GraphNode.ExpiresAt"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task EnforceRetentionAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var allNodes = await _graphStore.GetAllNodesAsync(cancellationToken);

        var expiredIds = allNodes
            .Where(n => n.ExpiresAt.HasValue && n.ExpiresAt.Value < now)
            .Select(n => n.Id)
            .ToList();

        if (expiredIds.Count > 0)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var erasureOrchestrator = scope.ServiceProvider.GetRequiredService<IErasureOrchestrator>();
            var receipt = await erasureOrchestrator.EraseByNodeIdsAsync(expiredIds, cancellationToken);
            _logger.LogInformation(
                "Retention enforcement: purged {Nodes} expired nodes",
                receipt.NodesDeleted);
        }
        else
        {
            _logger.LogDebug("Retention enforcement: no expired nodes found");
        }
    }
}
