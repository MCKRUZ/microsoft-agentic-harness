namespace Application.AI.Common.CQRS.Workflows.Submit;

/// <summary>
/// What a caller gets back from a successful submission: the server-minted identifier of the stored
/// workflow, plus the mapping from the caller's own step names to the identifiers the harness
/// assigned them.
/// </summary>
/// <remarks>
/// <para>
/// The name-to-id map exists because every identifier is minted server-side (see
/// <see cref="WorkflowDefinition"/>). Without it a caller has no way to correlate the step ids that
/// appear in later status and progress responses with the steps it authored — it would be reading a
/// run report about steps it cannot name. Returning the map at submission time is what keeps the
/// server-minted-id rule from making the run opaque to the system that submitted it.
/// </para>
/// <para>
/// There is deliberately no run identifier here. Submission stores a workflow; it does not start
/// one. A caller that wants both performs both operations, and each is authorized separately.
/// </para>
/// </remarks>
public sealed record SubmitWorkflowResult
{
    /// <summary>Server-minted identifier of the stored workflow, used to start and query it later.</summary>
    public required Guid WorkflowId { get; init; }

    /// <summary>
    /// The workflow's name as submitted, echoed so a caller storing only the id can label it without
    /// a second round trip.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Maps each submitted <c>WorkflowStep.Name</c> to the identifier the harness assigned it. Keys
    /// are exactly the names the caller supplied, so the map is directly usable as a lookup when
    /// interpreting later status responses.
    /// </summary>
    public required IReadOnlyDictionary<string, Guid> StepIds { get; init; }
}
