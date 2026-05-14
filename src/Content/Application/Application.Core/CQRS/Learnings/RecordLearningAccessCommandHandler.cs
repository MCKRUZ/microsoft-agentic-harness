using Application.AI.Common.Interfaces.Learnings;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.Learnings;

/// <summary>
/// Updates <see cref="Domain.AI.Learnings.LearningEntry.LastAccessedAt"/> on entries
/// retrieved during recall. Designed as a fire-and-forget side effect -- tolerates
/// missing entries silently since learnings may be deleted between recall and access recording.
/// </summary>
public sealed class RecordLearningAccessCommandHandler : IRequestHandler<RecordLearningAccessCommand, Result>
{
    private readonly ILearningsStore _store;
    private readonly ILogger<RecordLearningAccessCommandHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="RecordLearningAccessCommandHandler"/> class.</summary>
    public RecordLearningAccessCommandHandler(
        ILearningsStore store,
        ILogger<RecordLearningAccessCommandHandler> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result> Handle(RecordLearningAccessCommand request, CancellationToken cancellationToken)
    {
        foreach (var learningId in request.LearningIds)
        {
            var getResult = await _store.GetAsync(learningId, cancellationToken);
            if (!getResult.IsSuccess || getResult.Value is null)
            {
                _logger.LogDebug("Learning {LearningId} not found during access recording, skipping", learningId);
                continue;
            }

            var updated = getResult.Value with { LastAccessedAt = request.AccessedAt };
            var updateResult = await _store.UpdateAsync(updated, cancellationToken);
            if (!updateResult.IsSuccess)
                _logger.LogWarning("Failed to update LastAccessedAt for learning {LearningId}: {Errors}",
                    learningId, string.Join(", ", updateResult.Errors));
        }

        return Result.Success();
    }
}
