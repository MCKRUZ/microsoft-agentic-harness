using System.Diagnostics;
using Application.AI.Common.Interfaces.Learnings;
using Application.AI.Common.Interfaces.RAG;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Learnings;
using Domain.Common;
using Domain.Common.Config;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.Core.CQRS.Learnings;

/// <summary>
/// Retrieves and ranks learnings by blending semantic relevance with feedback weight and freshness.
/// Applies diversity injection to avoid echo-chamber effects in recall results.
/// </summary>
/// <remarks>
/// Scoring formula: <c>finalScore = (1 - alpha) * relevance + alpha * min(feedback * freshness, ceiling)</c>.
/// Learning content is embedded on-the-fly for cosine similarity since <see cref="LearningEntry"/>
/// does not store pre-computed embeddings.
/// </remarks>
public sealed class RecallQueryHandler : IRequestHandler<RecallQuery, Result<IReadOnlyList<WeightedLearning>>>
{
    private readonly ILearningsStore _store;
    private readonly ILearningDecayService _decayService;
    private readonly IEmbeddingService _embeddingService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RecallQueryHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="RecallQueryHandler"/> class.</summary>
    public RecallQueryHandler(
        ILearningsStore store,
        ILearningDecayService decayService,
        IEmbeddingService embeddingService,
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<AppConfig> options,
        TimeProvider timeProvider,
        ILogger<RecallQueryHandler> logger)
    {
        _store = store;
        _decayService = decayService;
        _embeddingService = embeddingService;
        _scopeFactory = scopeFactory;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<WeightedLearning>>> Handle(
        RecallQuery request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var config = _options.CurrentValue.AI.Learnings;

        if (!config.Enabled)
        {
            _logger.LogDebug("Learnings subsystem disabled, skipping recall");
            return Result<IReadOnlyList<WeightedLearning>>.Success(Array.Empty<WeightedLearning>());
        }

        var criteria = new LearningSearchCriteria { Scope = request.Scope };
        var searchResult = await _store.SearchAsync(criteria, cancellationToken);
        if (!searchResult.IsSuccess)
            return Result<IReadOnlyList<WeightedLearning>>.Fail(searchResult.Errors.ToArray());

        var candidates = searchResult.Value!;
        if (candidates.Count == 0)
            return Result<IReadOnlyList<WeightedLearning>>.Success(Array.Empty<WeightedLearning>());

        var queryEmbedding = await _embeddingService.EmbedQueryAsync(request.Context, cancellationToken);

        var embeddingTasks = candidates
            .Select(l => _embeddingService.EmbedQueryAsync(l.Content, cancellationToken))
            .ToArray();
        var contentEmbeddings = await Task.WhenAll(embeddingTasks);

        var scored = new List<WeightedLearning>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++)
        {
            var learning = candidates[i];
            var relevanceScore = CosineSimilarity(queryEmbedding.Span, contentEmbeddings[i].Span);
            var feedbackScore = learning.FeedbackWeight;
            var freshnessScore = await _decayService.CalculateFreshnessAsync(learning, cancellationToken);

            var finalScore = (1 - config.FeedbackAlpha) * relevanceScore
                           + config.FeedbackAlpha * Math.Min(feedbackScore * freshnessScore, config.FeedbackCeiling);

            scored.Add(new WeightedLearning
            {
                Learning = learning,
                RelevanceScore = relevanceScore,
                FeedbackScore = feedbackScore,
                FreshnessScore = freshnessScore,
                FinalScore = finalScore
            });
        }

        scored.Sort((a, b) => b.FinalScore.CompareTo(a.FinalScore));

        if (request.MinRelevance > 0)
            scored = scored.Where(r => r.RelevanceScore >= request.MinRelevance).ToList();

        List<WeightedLearning> results = ApplyDiversityInjection(scored, request.MaxResults, config.DiversityInjectionRatio);
        results = results.Take(request.MaxResults).ToList();

        _ = RecordAccessSafeAsync(results);

        sw.Stop();
        LearningsMetrics.Recalled.Add(results.Count);
        LearningsMetrics.RecallDurationMs.Record(sw.Elapsed.TotalMilliseconds);

        return Result<IReadOnlyList<WeightedLearning>>.Success(results);
    }

    /// <summary>
    /// Records access reinforcement for recalled learnings on a fresh DI scope.
    /// </summary>
    /// <remarks>
    /// This runs fire-and-forget after the query returns, so it must not depend on the
    /// request scope (which is disposed once the outer request completes). A dedicated
    /// scope is created via <see cref="IServiceScopeFactory"/> and a fresh
    /// <see cref="IMediator"/> resolved from it, so the dispatch survives request-scope
    /// disposal in short-lived hosts (console one-shots, per-request API scopes).
    /// Failures are swallowed at debug level — access reinforcement is best-effort.
    /// </remarks>
    private async Task RecordAccessSafeAsync(List<WeightedLearning> results)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            await mediator.Send(new RecordLearningAccessCommand
            {
                LearningIds = results.Select(r => r.Learning.LearningId).ToList(),
                AccessedAt = _timeProvider.GetUtcNow()
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to record learning access for {Count} learnings", results.Count);
        }
    }

    internal static List<WeightedLearning> ApplyDiversityInjection(
        List<WeightedLearning> sorted, int maxResults, double diversityRatio)
    {
        if (sorted.Count < 2 || diversityRatio <= 0)
            return sorted.Take(maxResults).ToList();

        var slotsToReplace = (int)Math.Floor(maxResults * diversityRatio);
        if (slotsToReplace < 1)
            return sorted.Take(maxResults).ToList();

        var topCount = Math.Min(maxResults - slotsToReplace, sorted.Count);
        var top = sorted.Take(topCount).ToList();
        var remaining = sorted.Skip(topCount).ToList();

        if (remaining.Count == 0)
            return top;

        var diversityPicks = remaining.OrderBy(_ => Random.Shared.Next()).Take(slotsToReplace).ToList();
        top.AddRange(diversityPicks);

        return top;
    }

    internal static double CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length || a.Length == 0)
            return 0.0;

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * (double)b[i];
            normA += a[i] * (double)a[i];
            normB += b[i] * (double)b[i];
        }

        var magnitude = Math.Sqrt(normA) * Math.Sqrt(normB);
        return magnitude == 0 ? 0.0 : dot / magnitude;
    }
}
