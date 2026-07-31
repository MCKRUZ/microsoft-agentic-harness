namespace Application.Core.CQRS.Evaluation.Runs;

/// <summary>What an accepted evaluation run gives the caller: an identifier to poll.</summary>
public sealed record StartEvalRunResult
{
    /// <summary>Server-minted identifier of the queued run.</summary>
    public required string JobId { get; init; }
}
