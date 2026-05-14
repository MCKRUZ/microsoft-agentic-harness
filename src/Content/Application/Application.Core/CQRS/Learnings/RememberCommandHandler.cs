using Application.AI.Common.Interfaces.Learnings;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Learnings;
using Domain.AI.Telemetry.Conventions;
using Domain.Common;
using Domain.Common.Config;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.Core.CQRS.Learnings;

/// <summary>
/// Captures a new learning by persisting it to the store and emitting a notification.
/// Maps <see cref="LearningCategory"/> to default <see cref="Domain.AI.Learnings.DecayClass"/>
/// when no explicit decay class is provided on the command.
/// </summary>
public sealed class RememberCommandHandler : IRequestHandler<RememberCommand, Result<LearningEntry>>
{
    private readonly ILearningsStore _store;
    private readonly ILearningNotificationChannel _notifications;
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RememberCommandHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="RememberCommandHandler"/> class.</summary>
    public RememberCommandHandler(
        ILearningsStore store,
        ILearningNotificationChannel notifications,
        IOptionsMonitor<AppConfig> options,
        TimeProvider timeProvider,
        ILogger<RememberCommandHandler> logger)
    {
        _store = store;
        _notifications = notifications;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<LearningEntry>> Handle(RememberCommand request, CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.AI.Learnings.Enabled)
        {
            _logger.LogDebug("Learnings subsystem disabled, skipping remember");
            return Result<LearningEntry>.Success(CreatePlaceholder(request));
        }

        var decayClass = request.DecayClass ?? MapCategoryToDecayClass(request.Category);
        var now = _timeProvider.GetUtcNow();

        var entry = new LearningEntry
        {
            LearningId = Guid.NewGuid(),
            Category = request.Category,
            DecayClass = decayClass,
            Scope = request.Scope,
            Content = request.Content,
            Source = request.Source,
            Provenance = request.Provenance,
            FeedbackWeight = 1.0,
            UpdateCount = 0,
            CreatedAt = now,
            LastAccessedAt = now,
            LastReinforcedAt = now
        };

        var saveResult = await _store.SaveAsync(entry, cancellationToken);
        if (!saveResult.IsSuccess)
            return Result<LearningEntry>.Fail(saveResult.Errors.ToArray());

        try
        {
            await _notifications.NotifyLearningCapturedAsync(entry, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to notify learning captured for {LearningId}", entry.LearningId);
        }

        LearningsMetrics.Remembered.Add(1,
            new KeyValuePair<string, object?>(LearningConventions.Category, request.Category.ToString().ToLowerInvariant()),
            new KeyValuePair<string, object?>(LearningConventions.Scope, GetScopeTag(request.Scope)));

        return Result<LearningEntry>.Success(entry);
    }

    internal static DecayClass MapCategoryToDecayClass(LearningCategory category) => category switch
    {
        LearningCategory.FactualCorrection => DecayClass.Permanent,
        LearningCategory.DomainKnowledge => DecayClass.Permanent,
        LearningCategory.InstructionUpdate => DecayClass.Stable,
        LearningCategory.StylePreference => DecayClass.Stable,
        LearningCategory.ToolUsagePattern => DecayClass.Stable,
        _ => DecayClass.Stable
    };

    private static string GetScopeTag(LearningScope scope) =>
        scope.AgentId is not null ? LearningConventions.ScopeValues.Agent :
        scope.TeamId is not null ? LearningConventions.ScopeValues.Team :
        LearningConventions.ScopeValues.Global;

    private LearningEntry CreatePlaceholder(RememberCommand request) => new()
    {
        LearningId = Guid.Empty,
        Category = request.Category,
        DecayClass = request.DecayClass ?? MapCategoryToDecayClass(request.Category),
        Scope = request.Scope,
        Content = request.Content,
        Source = request.Source,
        Provenance = request.Provenance,
        CreatedAt = _timeProvider.GetUtcNow()
    };
}
