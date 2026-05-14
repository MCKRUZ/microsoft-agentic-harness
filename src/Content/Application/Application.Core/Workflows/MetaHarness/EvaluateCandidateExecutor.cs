using Application.AI.Common.Interfaces.MetaHarness;
using Domain.Common.MetaHarness;
using Microsoft.Agents.AI.Workflows;

namespace Application.Core.Workflows.MetaHarness;

/// <summary>
/// Evaluates a proposed <see cref="HarnessCandidate"/> against the eval task suite and
/// updates the candidate with its score and status.
/// Second step in the optimization iteration workflow.
/// </summary>
/// <remarks>
/// When an <see cref="IEvalTaskSource"/> is registered in DI, tasks are loaded from it.
/// Otherwise the evaluation service receives an empty task list and is expected to use
/// its own configured task source.
/// </remarks>
public sealed class EvaluateCandidateExecutor(
    IEvaluationService evaluationService,
    IEvalTaskSource? evalTaskSource = null)
    : Executor<ProposalStepOutput, EvaluationStepOutput>("EvaluateCandidate")
{
    /// <summary>
    /// Runs the evaluation service against the candidate's snapshot, updates the candidate
    /// with the evaluation results, and transitions it to
    /// <see cref="HarnessCandidateStatus.Evaluated"/> status.
    /// </summary>
    /// <param name="message">The proposal output containing the candidate to evaluate.</param>
    /// <param name="context">The MAF workflow context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An <see cref="EvaluationStepOutput"/> with the evaluation result and scored candidate.</returns>
    public override async ValueTask<EvaluationStepOutput> HandleAsync(
        ProposalStepOutput message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var evalTasks = evalTaskSource != null
            ? await evalTaskSource.GetTasksAsync(message.Candidate, cancellationToken)
            : (IReadOnlyList<EvalTask>)[];

        var result = await evaluationService.EvaluateAsync(
            message.Candidate,
            evalTasks,
            cancellationToken);

        var evaluatedCandidate = message.Candidate with
        {
            BestScore = result.PassRate,
            TokenCost = result.TotalTokenCost,
            Status = HarnessCandidateStatus.Evaluated
        };

        return new EvaluationStepOutput(result, evaluatedCandidate);
    }
}
