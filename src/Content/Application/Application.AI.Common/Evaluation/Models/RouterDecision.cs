namespace Application.AI.Common.Evaluation.Models;

/// <summary>
/// The decision a router made for one evaluation input, normalized into a transport-agnostic
/// shape so the eval framework can score routing accuracy without depending on any specific
/// router's enum types.
/// </summary>
/// <remarks>
/// <para>
/// Produced by an <c>IRouterEvalProbe</c> and consumed by the <c>routing_accuracy</c> metric.
/// <see cref="Label"/> is the primary classification (e.g. the query-type or complexity-tier name)
/// that the labeled dataset asserts against. <see cref="SecondaryLabel"/> carries an additional
/// signal a router may emit (e.g. the RAG retrieval strategy alongside the query type) — recorded
/// for richer reporting but not scored by the default accuracy metric.
/// </para>
/// <para>
/// <see cref="Label"/> should be the router enum's member name (e.g. <c>"MultiHop"</c>,
/// <c>"Complex"</c>). The accuracy metric normalizes case and separators, so the dataset may use
/// any of <c>MultiHop</c> / <c>multi_hop</c> / <c>multihop</c> as the expected value.
/// </para>
/// </remarks>
public sealed record RouterDecision
{
    /// <summary>The primary routing classification — the router enum member name. Scored against the gold label.</summary>
    public required string Label { get; init; }

    /// <summary>The router's confidence in its decision (0.0–1.0), when reported. Surfaced for diagnostics.</summary>
    public double Confidence { get; init; }

    /// <summary>
    /// An optional secondary signal the router emits alongside the primary label
    /// (e.g. the selected retrieval strategy). Recorded for reporting; not scored by default.
    /// </summary>
    public string? SecondaryLabel { get; init; }

    /// <summary>Optional human-readable rationale from the router, useful when diagnosing a misroute.</summary>
    public string? Reasoning { get; init; }
}
