using Domain.AI.Learnings;
using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.Learnings;

/// <summary>
/// Updates a learning's feedback weight via EMA and optionally reinforces its content.
/// </summary>
public sealed record ImproveLearningCommand : IRequest<Result<LearningEntry>>
{
    /// <summary>ID of the learning to improve.</summary>
    public required Guid LearningId { get; init; }

    /// <summary>Feedback score (1.0-5.0). Incorporated into the learning's EMA weight.</summary>
    public required double FeedbackScore { get; init; }

    /// <summary>Optional updated content to reinforce the learning.</summary>
    public string? ReinforcementContent { get; init; }
}
