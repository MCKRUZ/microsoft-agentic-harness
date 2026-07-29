namespace Application.AI.Common.CQRS.Workflows.StartRun;

/// <summary>What a caller gets back when a run is accepted: the identifier it polls for progress.</summary>
/// <remarks>
/// Deliberately carries no status. The run is queued, not finished, and returning a status here would
/// invite a caller to treat acceptance as completion — the response says only that the work was taken.
/// </remarks>
public sealed record StartWorkflowRunResult
{
    /// <summary>Server-minted identifier of the queued run.</summary>
    public required string JobId { get; init; }
}
