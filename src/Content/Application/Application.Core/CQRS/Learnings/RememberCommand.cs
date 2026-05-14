using Domain.AI.Learnings;
using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.Learnings;

/// <summary>
/// Captures a new learning from corrections, drift events, escalation resolutions, or manual entries.
/// </summary>
public sealed record RememberCommand : IRequest<Result<LearningEntry>>
{
    /// <summary>The knowledge content to persist.</summary>
    public required string Content { get; init; }

    /// <summary>What kind of knowledge this learning represents.</summary>
    public required LearningCategory Category { get; init; }

    /// <summary>Visibility scope (agent, team, or global).</summary>
    public required LearningScope Scope { get; init; }

    /// <summary>What produced this learning.</summary>
    public required LearningSource Source { get; init; }

    /// <summary>Pipeline provenance metadata.</summary>
    public required LearningProvenance Provenance { get; init; }

    /// <summary>Override the default decay class. Null uses the category default.</summary>
    public DecayClass? DecayClass { get; init; }
}
