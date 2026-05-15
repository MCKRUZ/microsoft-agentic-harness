using Application.AI.Common.Interfaces.Learnings;
using Domain.AI.Learnings;
using Microsoft.Extensions.Logging;
using Presentation.AgentHub.AgUi;

namespace Presentation.AgentHub.Notifications;

/// <summary>
/// AG-UI notification channel for learning events. Translates domain learning
/// records into AG-UI SSE events and writes them to the active run's event stream.
/// </summary>
/// <remarks>
/// If no AG-UI run is active (e.g., the learning was captured from ConsoleUI or
/// a background pruning job), the notifier silently skips event emission.
/// </remarks>
public sealed class AgUiLearningNotifier : ILearningNotificationChannel
{
    private readonly IAgUiEventWriterAccessor _writerAccessor;
    private readonly ILogger<AgUiLearningNotifier> _logger;

    /// <summary>Initializes a new <see cref="AgUiLearningNotifier"/>.</summary>
    public AgUiLearningNotifier(
        IAgUiEventWriterAccessor writerAccessor,
        ILogger<AgUiLearningNotifier> logger)
    {
        _writerAccessor = writerAccessor;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task NotifyLearningCapturedAsync(LearningEntry learning, CancellationToken ct)
    {
        var writer = _writerAccessor.Writer;
        if (writer is null)
        {
            _logger.LogDebug("No AG-UI writer active; skipping learning-captured event for {LearningId}.",
                learning.LearningId);
            return;
        }

        var evt = new LearningCapturedEvent
        {
            LearningId = learning.LearningId.ToString(),
            Category = learning.Category.ToString(),
            AgentId = learning.Scope.AgentId,
            TeamId = learning.Scope.TeamId,
            IsGlobal = learning.Scope.IsGlobal,
            SourceDescription = learning.Source.SourceDescription,
        };

        try
        {
            await writer.WriteAsync(evt, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to write learning-captured event for {LearningId}.",
                learning.LearningId);
        }
    }

    /// <inheritdoc />
    public async Task NotifyLearningAppliedAsync(LearningEntry learning, string agentId, CancellationToken ct)
    {
        var writer = _writerAccessor.Writer;
        if (writer is null)
        {
            _logger.LogDebug("No AG-UI writer active; skipping learning-applied event for {LearningId}.",
                learning.LearningId);
            return;
        }

        var evt = new LearningAppliedEvent
        {
            LearningId = learning.LearningId.ToString(),
            AgentId = agentId,
            Category = learning.Category.ToString(),
        };

        try
        {
            await writer.WriteAsync(evt, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to write learning-applied event for {LearningId}.",
                learning.LearningId);
        }
    }
}
