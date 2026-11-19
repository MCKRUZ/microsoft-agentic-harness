using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.WorkMemory;
using Application.Common.Helpers;
using Application.Core.CQRS.Learnings;
using Domain.AI.Governance;
using Domain.AI.KnowledgeGraph.Scoping;
using Domain.AI.Learnings;
using Domain.AI.WorkMemory;
using Domain.Common;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.WorkMemory;
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
/// <strong>Per-owner synthesis (#968):</strong> a lesson distilled from one user's conversations must
/// reach only that user and be erasable with that user's data. The pass therefore reads the episodes
/// with the ambient request scope suspended (<see cref="IAmbientRequestScope.SuspendScope"/>, the seam
/// for privileged system reads), so a tenant-isolating store returns every owner's episodes rather than
/// none, then groups them by the owner and tenant each episode was stored under. Each group is
/// synthesized in its own DI scope with that owner set through <see cref="IKnowledgeScopeWriter"/>, so
/// the lessons it produces are owner-stamped, and they are scoped to that user
/// (<see cref="LearningScope.UserId"/>) so only that user recalls them. One group's episodes never reach
/// another group's synthesizer call.
/// </para>
/// <para>
/// The pass fails closed: a host with no <see cref="IKnowledgeScopeWriter"/> cannot attribute lessons to
/// their user, so that owner's group is skipped with an error rather than written owner-less and shared.
/// Episodes captured outside any user's request carry no owner; they are synthesized together into
/// global lessons every user recalls, with any inherited identity cleared. A synthesizer failure for one
/// group is logged and the pass moves on to the next.
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
    private readonly LastKnownGoodSetting<TimeSpan> _synthesisInterval;
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

        // A hot-reloaded value that fails validation makes CurrentValue throw; read it through a
        // last-known-good guard so that cannot escape the loop and stop the host (#821).
        _synthesisInterval = new LastKnownGoodSetting<TimeSpan>(
            () => TimeSpan.FromHours(config.CurrentValue.AI.WorkMemory.SynthesisIntervalHours),
            TimeSpan.FromHours(new WorkMemoryConfig().SynthesisIntervalHours),
            logger,
            "AppConfig:AI:WorkMemory:SynthesisIntervalHours");
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_synthesisInterval.Current, stoppingToken);

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
    /// Runs a single synthesis pass immediately: read recent episodes, synthesize lessons per owner, gate
    /// them, and persist survivors. Exposed so the pass can be triggered on demand (tests, manual ops)
    /// without waiting for the interval. Creates and owns its own DI scopes.
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

        var episodesResult = await ReadEpisodesAsync(cfg, ct);
        if (!episodesResult.IsSuccess)
            return Result<int>.Fail(episodesResult.Errors.ToArray());

        var episodes = episodesResult.Value!; // non-null: Result<T>.Value is populated whenever IsSuccess is true
        if (episodes.Count == 0)
        {
            _logger.LogDebug("Work-memory synthesis: no episodes in the {Hours}h look-back window", cfg.SynthesisLookbackHours);
            return Result<int>.Success(0);
        }

        // One id per pass, shared by every lesson it produces, so the audit trail can correlate all
        // lessons back to the synthesis run that created them (a per-lesson random id would not).
        var runId = Guid.NewGuid().ToString();

        var groups = episodes.GroupBy(e => new EpisodeOwner(
            ScopeIdentity.Canonicalize(e.OwnerId), ScopeIdentity.Canonicalize(e.TenantId)));

        var stored = 0;
        foreach (var group in groups)
            stored += await SynthesizeGroupAsync(group.Key, group.ToList(), runId, cfg, ct);

        _logger.LogInformation(
            "Work-memory synthesis: {Stored} lessons stored from {Episodes} episodes",
            stored, episodes.Count);

        return Result<int>.Success(stored);
    }

    /// <summary>
    /// Reads the look-back window's episodes for every owner. The ambient request scope is suspended so a
    /// tenant-isolating store applies no caller's filter: without it, the store would see a scope with no
    /// user and hide every owner-stamped episode, and a pass triggered inside a request would see only
    /// that caller's episodes.
    /// </summary>
    private async Task<Result<IReadOnlyList<WorkEpisode>>> ReadEpisodesAsync(WorkMemoryConfig cfg, CancellationToken ct)
    {
        using var suspended = _ambientScope.SuspendScope();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var episodeStore = scope.ServiceProvider.GetRequiredService<IWorkEpisodeStore>();

        var criteria = new WorkEpisodeSearchCriteria
        {
            CreatedAfter = _timeProvider.GetUtcNow() - TimeSpan.FromHours(cfg.SynthesisLookbackHours),
            Limit = cfg.MaxEpisodesPerRun
        };

        return await episodeStore.SearchAsync(criteria, ct);
    }

    /// <summary>
    /// Synthesizes one owner's episodes into lessons and persists them as that owner, in a DI scope of
    /// their own. Returns the number of lessons stored.
    /// </summary>
    private async Task<int> SynthesizeGroupAsync(
        EpisodeOwner owner, IReadOnlyList<WorkEpisode> episodes, string runId, WorkMemoryConfig cfg, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        using var ambient = _ambientScope.BeginScope(scope.ServiceProvider);

        var writer = scope.ServiceProvider.GetService<IKnowledgeScopeWriter>();
        if (owner.UserId is not null && writer is null)
        {
            _logger.LogError(
                "Work-memory synthesis skipped {Count} episodes: this host registers no {Writer}, so their "
                + "lessons cannot be attributed to the user they came from and must not be written shared.",
                episodes.Count, nameof(IKnowledgeScopeWriter));
            return 0;
        }

        // Set even for the owner-less group: a null user replaces any identity inherited from a caller,
        // so global lessons are never stamped with whoever triggered the pass.
        using var identity = writer?.SetScope(userId: owner.UserId, tenantId: owner.TenantId);

        var synthesizer = scope.ServiceProvider.GetRequiredService<IWorkEpisodeSynthesizer>();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        IReadOnlyList<SynthesizedLesson> lessons;
        try
        {
            lessons = await synthesizer.SynthesizeAsync(episodes, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Work-memory synthesis failed for a group of {Count} episodes", episodes.Count);
            return 0;
        }

        var stored = 0;
        foreach (var lesson in lessons)
        {
            if (MeetsConfidenceFloor(lesson, cfg.MinConfidenceToStore)
                && await PersistLessonAsync(mediator, lesson, owner.UserId, runId, ct))
                stored++;
        }

        _logger.LogDebug(
            "Work-memory synthesis: {Stored}/{Candidates} lessons stored from a group of {Episodes} episodes",
            stored, lessons.Count, episodes.Count);
        return stored;
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

    private async Task<bool> PersistLessonAsync(
        IMediator mediator, SynthesizedLesson lesson, string? ownerId, string runId, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        var command = new RememberCommand
        {
            Content = lesson.Content,
            Category = lesson.Category,
            // Global tier so the existing index finds it; the user axis narrows it to the owner when there
            // is one. RememberCommandValidator requires an agent, team or global tier.
            Scope = new LearningScope { IsGlobal = true, UserId = ownerId },
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

    /// <summary>The canonical owner and tenant a group of episodes was stored under.</summary>
    private readonly record struct EpisodeOwner(string? UserId, string? TenantId);
}
