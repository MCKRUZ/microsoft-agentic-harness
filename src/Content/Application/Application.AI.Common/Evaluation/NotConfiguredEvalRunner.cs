using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Domain.AI.Evaluation;

namespace Application.AI.Common.Evaluation;

/// <summary>
/// Fail-fast default <see cref="IEvalRunner"/> for hosts that do not opt into the evaluation
/// framework. Invoking it throws — see <c>NotConfiguredRolloutRunner</c> for the rationale.
/// </summary>
/// <remarks>
/// <para>
/// The evaluation framework (real <c>EvalRunner</c>, metrics, reporters, YAML loader) is
/// registered only by the EvalRunner host via <c>AddEvaluationDependencies()</c>. But
/// <c>RunEvalSuiteCommandHandler</c> is discovered by global MediatR assembly scanning and
/// therefore registered in every host. This default keeps that handler constructible so the
/// composition root passes <c>ValidateOnBuild</c>.
/// </para>
/// <para>
/// It deliberately throws rather than returning an empty report: an empty
/// <see cref="EvalRunReport"/> would read as "the suite ran and found nothing", silently
/// masking a mis-wired host. In a correctly configured host this type is never resolved —
/// the EvalRunner host's <c>AddSingleton&lt;IEvalRunner, EvalRunner&gt;</c> wins via
/// last-registration-wins — so the throw is only ever reached if the eval command is
/// dispatched somewhere the framework was never wired.
/// </para>
/// </remarks>
public sealed class NotConfiguredEvalRunner : IEvalRunner
{
    /// <inheritdoc />
    public Task<EvalRunReport> RunAsync(
        IReadOnlyList<EvalDataset> datasets,
        EvalRunOptions options,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "No IEvalRunner is configured. The evaluation framework is opt-in: call " +
            "services.AddEvaluationDependencies() from the host that runs evaluations " +
            "(e.g. Presentation.EvalRunner) before dispatching RunEvalSuiteCommand.");
}
