using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.Common.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.RAG.GraphRag;

/// <summary>
/// Background service that periodically drives <see cref="IMemoryDecayService"/>, applying
/// time-based decay to cross-session memory weights and pruning memories that fall below the
/// configured threshold.
/// </summary>
/// <remarks>
/// <para>
/// Without this scheduler the decay service is inert: <see cref="IMemoryDecayService.ApplyDecayAsync"/>
/// only runs when something calls it, so in a live host stored weights never age and low-value
/// memories accumulate indefinitely. This hosted service is what makes the decay tier system
/// actually run over time.
/// </para>
/// <para>
/// <strong>Opt-in.</strong> Registered only when
/// <see cref="Domain.Common.Config.AI.RAG.MemoryDecaySchedulerConfig.Enabled"/> is <c>true</c>.
/// A fresh clone leaves it disabled so cloning the template does not silently start mutating
/// stored memory weights.
/// </para>
/// <para>
/// <strong>Scope per tick.</strong> The host runs with <c>ValidateScopes=true</c>, so this
/// singleton hosted service must not capture scoped dependencies. Each tick creates a fresh
/// <see cref="IServiceScope"/> and resolves <see cref="IMemoryDecayService"/> from it, keeping
/// the wiring correct even if a consumer re-lifetimes the decay service (or its graph backend)
/// to a scoped registration.
/// </para>
/// <para>
/// <strong>Cadence.</strong> The interval comes from configuration (never hardcoded). Each pass
/// runs <see cref="IMemoryDecayService.ApplyDecayAsync"/> followed by
/// <see cref="IMemoryDecayService.PruneAsync"/> using the configured prune threshold, completing
/// the decay-then-prune cycle.
/// </para>
/// <para>
/// <strong>Failure isolation.</strong> Exceptions from a pass are logged and swallowed so one bad
/// tick never tears down the host or stops future passes.
/// </para>
/// </remarks>
public sealed class MemoryDecayScheduler : BackgroundService
{
    private static readonly TimeSpan _fallbackInterval = TimeSpan.FromHours(6);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<MemoryDecayScheduler> _logger;

    /// <summary>Initializes a new <see cref="MemoryDecayScheduler"/>.</summary>
    /// <param name="scopeFactory">Factory used to create a fresh DI scope per tick.</param>
    /// <param name="config">Live configuration providing cadence and prune threshold.</param>
    /// <param name="logger">Logger for pass-level operational metrics.</param>
    public MemoryDecayScheduler(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<AppConfig> config,
        ILogger<MemoryDecayScheduler> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = ResolveInterval();
        using var timer = new PeriodicTimer(interval);

        _logger.LogInformation(
            "MemoryDecayScheduler started; running a decay+prune pass every {Interval}.", interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await RunPassAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected during host shutdown.
        }
    }

    private async Task RunPassAsync(CancellationToken stoppingToken)
    {
        var pruneThreshold = _config.CurrentValue.AI.Rag.CrossSessionMemory.PruneThreshold;

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var decayService = scope.ServiceProvider.GetRequiredService<IMemoryDecayService>();

            await decayService.ApplyDecayAsync(stoppingToken).ConfigureAwait(false);
            await decayService.PruneAsync(pruneThreshold, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown — propagate by exiting the loop.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MemoryDecayScheduler pass failed; the next scheduled pass will retry.");
        }
    }

    private TimeSpan ResolveInterval()
    {
        var configured = _config.CurrentValue.AI.Rag.CrossSessionMemory.DecayScheduler.Interval;
        return configured > TimeSpan.Zero ? configured : _fallbackInterval;
    }
}
