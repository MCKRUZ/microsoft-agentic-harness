using System.Collections.Immutable;
using Application.AI.Common.Evaluation.Models;
using Domain.AI.Evaluation;

namespace Application.AI.Common.Evaluation.Interfaces;

/// <summary>
/// Invokes the harness for one evaluation case and returns the result.
/// Abstracts the "how do we run the harness?" detail so the runner stays
/// transport-agnostic and unit-testable.
/// </summary>
/// <remarks>
/// <para>
/// The production implementation wraps <c>ExecuteAgentTurnCommand</c> via
/// <c>IMediator</c>, so the full MediatR pipeline (content safety, tool boundary,
/// audit, etc.) runs exactly as it would in a real agent turn. This is intentional —
/// eval should exercise the production code path, not a stripped-down shadow of it.
/// </para>
/// <para>
/// Test implementations can return canned <see cref="AgentInvocationResult"/>s for
/// runner unit tests without engaging the real agent stack.
/// </para>
/// </remarks>
public interface IAgentInvoker
{
    /// <summary>
    /// The <see cref="EvalCase.InvocationOverrides"/> keys this invoker actually reads. Empty by
    /// default — only an invoker that reads case-author-supplied overrides needs to override this.
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="Application.AI.Common.Evaluation.Interfaces.IEvalMetric.RecognizedParameterKeys"/>
    /// (#437): <c>InvocationOverrides</c> is the same free-form, hand-authored
    /// <c>Dictionary&lt;string,string&gt;</c> shape as <c>MetricSpec.Parameters</c>,
    /// parsed from the same YAML files, and resolved via the same fail-soft "typo silently falls
    /// back to default" accessors — so it carries the identical #423/#410 risk. This property lets a
    /// validation pass compare a case's declared override keys against the resolved invoker's own
    /// declared set the same way #423 does for metric parameters, without needing to read
    /// <see cref="InvokeAsync"/>'s implementation to find out what it actually reads.
    /// </remarks>
    IReadOnlySet<string> RecognizedOverrideKeys => ImmutableHashSet<string>.Empty;

    /// <summary>
    /// Invokes the harness for the given case.
    /// </summary>
    /// <param name="case">The case being evaluated. Carries input and optional invocation overrides.</param>
    /// <param name="runLevelOverrides">Run-wide invocation overrides (merged under case-level overrides).</param>
    /// <param name="forceDeterministic">When true, temperature is forced to 0 regardless of overrides.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The invocation result. Never null; <see cref="AgentInvocationResult.Success"/> indicates outcome.</returns>
    Task<AgentInvocationResult> InvokeAsync(
        EvalCase @case,
        IReadOnlyDictionary<string, string>? runLevelOverrides,
        bool forceDeterministic,
        CancellationToken cancellationToken);
}
