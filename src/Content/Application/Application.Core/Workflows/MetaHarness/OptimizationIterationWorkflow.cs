using Application.AI.Common.Interfaces.MetaHarness;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace Application.Core.Workflows.MetaHarness;

/// <summary>
/// Static factory for building the meta-harness optimization iteration workflow.
/// Each iteration follows a sequential <c>Propose --> Evaluate --> Score</c> pipeline.
/// The outer optimization loop (multiple iterations) remains in the MediatR command handler.
/// </summary>
/// <remarks>
/// <para>
/// Workflow graph: <c>ProposeChanges</c> --> <c>EvaluateCandidate</c> --> <c>ScoreCandidate</c>
/// </para>
/// <para>
/// Each executor is stateless with respect to the workflow graph; all state flows
/// through the typed messages. The <see cref="IHarnessProposer"/>,
/// <see cref="IEvaluationService"/>, and <see cref="IHarnessCandidateRepository"/>
/// dependencies are resolved from the service provider at build time.
/// </para>
/// </remarks>
public static class OptimizationIterationWorkflow
{
    /// <summary>
    /// Builds the optimization iteration workflow with dependencies resolved from
    /// the provided service provider.
    /// </summary>
    /// <param name="services">
    /// Service provider used to resolve <see cref="IHarnessProposer"/>,
    /// <see cref="IEvaluationService"/>, and <see cref="IHarnessCandidateRepository"/>.
    /// </param>
    /// <returns>A configured <see cref="Workflow"/> representing one optimization iteration.</returns>
    public static Workflow Build(IServiceProvider services)
    {
        var proposer = services.GetRequiredService<IHarnessProposer>();
        var evaluationService = services.GetRequiredService<IEvaluationService>();
        var candidateRepository = services.GetRequiredService<IHarnessCandidateRepository>();
        var evalTaskSource = services.GetService<IEvalTaskSource>();

        var propose = new ProposeChangesExecutor(proposer, candidateRepository);
        var evaluate = new EvaluateCandidateExecutor(evaluationService, evalTaskSource);
        var score = new ScoreCandidateExecutor(candidateRepository);

        return new WorkflowBuilder(propose)
            .WithName("OptimizationIteration")
            .WithDescription("Single propose-evaluate-score iteration of meta-harness optimization")
            .AddEdge(propose, evaluate)
            .AddEdge(evaluate, score)
            .WithOutputFrom(score)
            .Build();
    }
}
