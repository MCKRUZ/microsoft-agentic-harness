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
    /// The <see cref="EvalCase.InvocationOverrides"/> keys this invoker reads <em>for this case</em>.
    /// Defaults to <see cref="RecognizedOverrideKeys"/>; an invoker whose dispatch depends on the case
    /// (one that routes some cases elsewhere) overrides it so a key that is read only on the other path
    /// is reported for this one (#503).
    /// </summary>
    /// <param name="case">The case whose overrides are being checked.</param>
    IReadOnlySet<string> RecognizedOverrideKeysFor(EvalCase @case) => RecognizedOverrideKeys;

    /// <summary>
    /// Problems this invoker can see in the cases before any of them runs, each worded as a report warning: for
    /// example a case naming an agent this host does not have. Empty by default.
    /// </summary>
    /// <remarks>
    /// The cases still run, and one this flags errors with its own reason. The warning exists so an operator sees the
    /// cause once, at the top of the report, instead of reading it off every errored row (#1071). An invoker that
    /// decorates another must pass the cases it hands on to the inner invoker's check, or the check never runs.
    /// </remarks>
    /// <param name="cases">The cases the run is about to invoke.</param>
    /// <param name="runLevelOverrides">Run-wide invocation overrides, merged under case-level ones as in <see cref="InvokeAsync"/>.</param>
    IReadOnlyList<string> CheckBeforeRun(
        IReadOnlyList<EvalCase> cases,
        IReadOnlyDictionary<string, string>? runLevelOverrides) => [];

    /// <summary>
    /// Invokes the harness for the given case.
    /// </summary>
    /// <param name="case">The case being evaluated. Carries input and optional invocation overrides.</param>
    /// <param name="runLevelOverrides">Run-wide invocation overrides (merged under case-level overrides).</param>
    /// <param name="forceDeterministic">When true, temperature is forced to 0 regardless of overrides.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// The invocation result. Never null. <see cref="AgentInvocationResult.Success"/> is false when the agent ran and
    /// did not answer, for example because screening refused the input: an outcome metrics can score.
    /// </returns>
    /// <exception cref="Application.AI.Common.Exceptions.AgentInvocationFaultException">
    /// The harness could not run the agent turn at all (not configured, internal error). Callers treat this as a
    /// broken case rather than a wrong answer.
    /// </exception>
    Task<AgentInvocationResult> InvokeAsync(
        EvalCase @case,
        IReadOnlyDictionary<string, string>? runLevelOverrides,
        bool forceDeterministic,
        CancellationToken cancellationToken);
}
