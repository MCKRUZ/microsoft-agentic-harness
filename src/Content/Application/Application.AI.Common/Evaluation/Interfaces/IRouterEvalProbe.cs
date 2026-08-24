using System.Collections.Immutable;
using Application.AI.Common.Evaluation.Models;

namespace Application.AI.Common.Evaluation.Interfaces;

/// <summary>
/// Adapts a single routing/classification component so the evaluation framework can feed it a
/// labeled input and read back its decision, without the framework taking a dependency on the
/// router's concrete types. One probe wraps one router (e.g. the RAG query-type classifier or the
/// agent task-complexity classifier).
/// </summary>
/// <remarks>
/// <para>
/// Probes are discovered as an enumerable and indexed by <see cref="Key"/> (the same pattern the
/// eval runner uses for metrics), so a consumer can add coverage for a new router simply by
/// registering another <see cref="IRouterEvalProbe"/> — no change to the runner or invoker.
/// </para>
/// <para>
/// A probe calls only the underlying router; it does not run the full agent turn. This isolates
/// the routing decision so the <c>routing_accuracy</c> metric measures the router itself rather
/// than the end-to-end answer. Implementations should be safe to invoke concurrently.
/// </para>
/// </remarks>
public interface IRouterEvalProbe
{
    /// <summary>
    /// The stable key by which an eval case selects this probe via its
    /// <c>target: "router:&lt;key&gt;"</c> invocation override (e.g. <c>"query_type"</c>,
    /// <c>"task_complexity"</c>).
    /// </summary>
    string Key { get; }

    /// <summary>
    /// The <see cref="Domain.AI.Evaluation.EvalCase.InvocationOverrides"/> tuning keys this probe
    /// actually reads from <see cref="ClassifyAsync"/>'s <c>parameters</c>. Empty by default — only a
    /// probe that reads case-author-supplied tuning keys needs to override this.
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="IEvalMetric.RecognizedParameterKeys"/>/<see cref="IAgentInvoker.RecognizedOverrideKeys"/>
    /// (#437). <c>RouterEvalInvoker</c> (Infrastructure) unions every registered probe's declared set
    /// into its own <c>RecognizedOverrideKeys</c>, since which probe a given case's <c>parameters</c>
    /// actually reach depends on that case's own <c>target</c> override.
    /// </remarks>
    IReadOnlySet<string> RecognizedOverrideKeys => ImmutableHashSet<string>.Empty;

    /// <summary>
    /// Runs the wrapped router for one input and returns its decision.
    /// </summary>
    /// <param name="input">The case input — typically the user query or task description.</param>
    /// <param name="parameters">
    /// The case's invocation overrides, passed through verbatim. Probes may read optional tuning
    /// keys declared via <see cref="RecognizedOverrideKeys"/> (e.g. <c>tool_count</c> for a router
    /// that needs turn context); unknown keys are ignored.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The router's normalized decision. Implementations should fall back to the router's
    /// own conservative default rather than throwing when the underlying call fails.</returns>
    Task<RouterDecision> ClassifyAsync(
        string input,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken);
}
