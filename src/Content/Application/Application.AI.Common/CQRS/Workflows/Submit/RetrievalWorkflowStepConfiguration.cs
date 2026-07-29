using Domain.AI.Planner;
using Domain.AI.RAG.Enums;

namespace Application.AI.Common.CQRS.Workflows.Submit;

/// <summary>
/// Configuration for a step that queries the retrieval pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Named with a <c>Workflow</c> infix rather than matching its five siblings, because
/// <c>Domain.AI.Planner.RetrievalStepConfiguration</c> already exists and the mapper references both.
/// Two types with the same name in one file, distinguished only by namespace, is the kind of ambiguity
/// that produces a plausible-looking edit against the wrong one.
/// </para>
/// <para>
/// <strong>The collection is not caller-selectable.</strong> The domain type carries a
/// <c>CollectionName</c>, and it is absent here on purpose: naming a collection directly is the
/// cross-tenant read primitive that scoped collections exist to prevent. The collection is derived
/// server-side from the caller's scope, so a workflow can only ever retrieve from what its submitter
/// could already read.
/// </para>
/// <para>
/// Retrieval is authorized as a capability in its own right, under a reserved name, through the same
/// governor as any tool — so a caller whose envelope withholds it gets a step that fails closed rather
/// than a silent full-corpus read.
/// </para>
/// </remarks>
public sealed record RetrievalWorkflowStepConfiguration : WorkflowStepConfiguration
{
    /// <inheritdoc />
    public override StepType StepType => StepType.Retrieval;

    /// <summary>The query text to retrieve against.</summary>
    public required string Query { get; init; }

    /// <summary>
    /// Optional retrieval strategy. When omitted, the host's configured default applies.
    /// </summary>
    public RetrievalStrategy? Strategy { get; init; }

    /// <summary>
    /// Optional maximum number of results. Bounded by the host's ceiling; a request above it is
    /// rejected rather than clamped, so a caller is never silently given less context than it asked for.
    /// </summary>
    public int? TopK { get; init; }

    /// <summary>
    /// Whether to fan out across multiple retrieval sources — vector, keyword, and graph — rather than
    /// the default single source. Costs more per step, so it is opt-in.
    /// </summary>
    public bool UseMultiSource { get; init; }
}
