using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.WorkMemory;
using Application.Core.CQRS.Learnings;
using Domain.AI.Governance;
using Domain.AI.Learnings;
using Domain.AI.WorkMemory;
using Domain.Common;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.WorkMemory;

/// <summary>
/// The overnight synthesis pass (PR2 of self-improving work memory). On a configured interval, reads
/// the recent batch of <see cref="WorkEpisode"/> records, distills them into reusable lessons via
/// <see cref="IWorkEpisodeSynthesizer"/>, security-gates each candidate, and persists the survivors as
/// <see cref="LearningEntry"/> records through the standard Learnings write path (<c>RememberCommand</c>).
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <c>LearningsPruningBackgroundService</c> for the loop/interval/cancellation shape. Because
/// the synthesis write path (<see cref="IMediator"/>) is request-scoped, each cycle runs inside a fresh
/// DI scope created from <see cref="IServiceScopeFactory"/>; the scope is also re-established as the
/// ambient request scope so tenant/compliance-aware stores resolve identity from a live provider —
/// the same pattern <c>KnowledgeExtractionBehavior</c> uses for post-turn writes.
/// </para>
/// <para>
/// <strong>Security:</strong> synthesized lessons are model-generated from raw session content, and are
/// stored where they will be auto-loaded into future prompts, so every candidate is scanned before it
/// is persisted. That scan is <em>not</em> performed here: it happens inside <c>RememberCommandHandler</c>,
/// the learnings write chokepoint, which runs the same <c>IMemoryWriteGate</c> as the knowledge-memory
/// channel and can now quarantine as well as reject (issue #338).
/// </para>
/// <para>
/// This service previously ran its own scan, because at the time the learnings store had no quarantine
/// tier and a flagged lesson could only be dropped. Both bands are now available at the chokepoint, so
/// the local copy was removed rather than left to drift: a duplicated threshold ladder is one that
/// eventually disagrees with the original, and this one already did — it dropped at <c>High</c> where
/// the gate quarantines from <c>Medium</c> and rejects at <c>Critical</c>. Quarantining is also the
/// better outcome for this source: a flagged lesson is retained for incident response instead of
/// vanishing, and is still never recalled. What remains here is the confidence floor, which is a
/// synthesis-quality judgement and genuinely local.
/// </para>
/// <para>
/// <strong>Tenant scope:</strong> like the sibling background jobs (<c>LearningsPruningBackgroundService</c>,
/// retention enforcement), the pass runs with no inbound request identity, so it operates over the
/// store's unscoped view. Per-tenant synthesis is a future extension point if a consumer enables
/// multi-tenant isolation and needs lessons partitioned by tenant.
/// </para>
/// </remarks>
public sealed class WorkMemorySynthesisBackgroundService : BackgroundService
{
    private const string OriginPipeline = "work_memory_synthesis";
    private const string OriginTask = "overnight_synthesis";
    private const string SourceDescription = "Overnight work-memory synthesis";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAmbientRequestScope _ambientScope;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WorkMemorySynthesisBackgroundService> _logger;

    /// <summary>Initializes a new instance of the <see cref="WorkMemorySynthesisBackgroundService"/> class.</summary>
    public WorkMemorySynthesisBackgroundService(
        IServiceScopeFactory scopeFactory,
        IAmbientRequestScope ambientScope,
        IOptionsMonitor<AppConfig> config,
        TimeProvider timeProvider,
        ILogger<WorkMemorySynthesisBackgroundService> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(ambientScope);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _ambientScope = ambientScope;
        _config = config;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromHours(_config.CurrentValue.AI.WorkMemory.SynthesisIntervalHours);
            await Task.Delay(interval, stoppingToken);

            try
            {
                var result = await SynthesizeNowAsync(stoppingToken);
                if (result.IsSuccess)
                    _logger.LogInformation("Work-memory synthesis cycle complete: {Count} lessons stored", result.Value);
                else
                    _logger.LogWarning("Work-memory synthesis cycle failed: {Errors}", string.Join(", ", result.Errors));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error during work-memory synthesis cycle");
            }
        }
    }

    /// <summary>
    /// Runs a single synthesis pass immediately: read recent episodes, synthesize lessons, gate them,
    /// and persist survivors. Exposed so the pass can be triggered on demand (tests, manual ops) without
    /// waiting for the interval. Creates and owns its own DI scope.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of lessons stored, or a failure when reading episodes fails.</returns>
    public async Task<Result<int>> SynthesizeNowAsync(CancellationToken ct)
    {
        var ai = _config.CurrentValue.AI;
        var cfg = ai.WorkMemory;

        // Synthesis persists lessons through the Learnings store (RememberCommand). With Learnings
        // disabled, RememberCommandHandler short-circuits to a no-op success placeholder — so a pass
        // would call the LLM and report lessons "stored" that were silently dropped. Refuse to run
        // (and skip the LLM cost) when there is no live write target. Checked each pass so a runtime
        // IOptionsMonitor toggle of Learnings is honored without restarting the service.
        if (!ai.Learnings.Enabled)
        {
            _logger.LogWarning(
                "Work-memory synthesis is enabled but the Learnings subsystem is disabled; skipping the " +
                "pass. Lessons have no persistence target until AppConfig:AI:Learnings:Enabled is true.");
            return Result<int>.Success(0);
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        using var _ = _ambientScope.BeginScope(scope.ServiceProvider);

        var episodeStore = scope.ServiceProvider.GetRequiredService<IWorkEpisodeStore>();
        var synthesizer = scope.ServiceProvider.GetRequiredService<IWorkEpisodeSynthesizer>();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var criteria = new WorkEpisodeSearchCriteria
        {
            CreatedAfter = _timeProvider.GetUtcNow() - TimeSpan.FromHours(cfg.SynthesisLookbackHours),
            Limit = cfg.MaxEpisodesPerRun
        };

        var episodesResult = await episodeStore.SearchAsync(criteria, ct);
        if (!episodesResult.IsSuccess)
            return Result<int>.Fail(episodesResult.Errors.ToArray());

        var episodes = episodesResult.Value!; // non-null: Result<T>.Value is populated whenever IsSuccess is true
        if (episodes.Count == 0)
        {
            _logger.LogDebug("Work-memory synthesis: no episodes in the {Hours}h look-back window", cfg.SynthesisLookbackHours);
            return Result<int>.Success(0);
        }

        var lessons = await synthesizer.SynthesizeAsync(episodes, ct);

        // One id per pass, shared by every lesson it produces, so the audit trail can correlate all
        // lessons back to the synthesis run that created them (a per-lesson random id would not).
        var runId = Guid.NewGuid().ToString();

        var stored = 0;
        foreach (var lesson in lessons)
        {
            if (!MeetsConfidenceFloor(lesson, cfg.MinConfidenceToStore))
                continue;

            var remembered = await PersistLessonAsync(mediator, lesson, runId, ct);
            if (remembered)
                stored++;
        }

        _logger.LogInformation(
            "Work-memory synthesis: {Stored}/{Candidates} lessons stored from {Episodes} episodes",
            stored, lessons.Count, episodes.Count);

        return Result<int>.Success(stored);
    }

    /// <summary>
    /// Applies the synthesis-quality floor: a lesson the synthesizer is not confident enough about is
    /// not worth storing. Content safety is deliberately not decided here — see the class remarks.
    /// </summary>
    private bool MeetsConfidenceFloor(SynthesizedLesson lesson, double minConfidence)
    {
        if (lesson.Confidence >= minConfidence)
            return true;

        _logger.LogDebug("Dropping synthesized lesson below confidence floor ({Confidence} < {Floor})",
            lesson.Confidence, minConfidence);
        return false;
    }

    private async Task<bool> PersistLessonAsync(IMediator mediator, SynthesizedLesson lesson, string runId, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        var command = new RememberCommand
        {
            Content = lesson.Content,
            Category = lesson.Category,
            Scope = new LearningScope { IsGlobal = true },
            Source = new LearningSource
            {
                SourceType = LearningSourceType.AgentSelfImprovement,
                SourceId = runId,
                SourceDescription = SourceDescription
            },
            Provenance = new LearningProvenance
            {
                OriginPipeline = OriginPipeline,
                OriginTask = OriginTask,
                OriginTimestamp = now,
                Confidence = lesson.Confidence
            }
        };

        try
        {
            var result = await mediator.Send(command, ct);
            if (result.IsSuccess)
                return true;

            _logger.LogWarning("Failed to persist synthesized lesson: {Errors}", string.Join(", ", result.Errors));
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Error persisting synthesized lesson");
            return false;
        }
    }
}
