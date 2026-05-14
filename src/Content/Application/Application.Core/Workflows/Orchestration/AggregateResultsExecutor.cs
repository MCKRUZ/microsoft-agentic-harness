using System.Text;
using Microsoft.Agents.AI.Workflows;

namespace Application.Core.Workflows.Orchestration;

/// <summary>
/// Fan-in aggregator that incrementally collects <see cref="AgentStepResult"/> messages
/// from parallel agent executors and produces an <see cref="AggregatedAgentResults"/> summary.
/// </summary>
/// <remarks>
/// <para>
/// Built on <see cref="AggregatingExecutor{TInput, TAggregate}"/>, which handles the
/// incremental accumulation pattern required by
/// <see cref="WorkflowBuilder.AddFanInBarrierEdge(IEnumerable{ExecutorBinding}, ExecutorBinding)"/>.
/// The barrier holds messages until every source executor has produced at least one result,
/// then streams them to this executor one at a time. Each invocation of the aggregation
/// function folds a new <see cref="AgentStepResult"/> into the running
/// <see cref="AggregatedAgentResults"/>.
/// </para>
/// <para>
/// The <see cref="AggregatedAgentResults.CombinedOutput"/> is built by concatenating all
/// successful agent outputs, each prefixed with the agent name for identification.
/// Failed agents are counted but their error messages are excluded from the combined output.
/// </para>
/// </remarks>
public sealed class AggregateResultsExecutor()
    : AggregatingExecutor<AgentStepResult, AggregatedAgentResults>(
        "aggregate_results",
        Aggregate)
{
    private static AggregatedAgentResults? Aggregate(
        AggregatedAgentResults? current,
        AgentStepResult incoming)
    {
        var results = current is null
            ? new List<AgentStepResult>(4) { incoming }
            : [.. current.Results, incoming];

        var succeededCount = (current?.SucceededCount ?? 0) + (incoming.Succeeded ? 1 : 0);
        var failedCount = (current?.FailedCount ?? 0) + (incoming.Succeeded ? 0 : 1);

        return new AggregatedAgentResults(
            results,
            BuildCombinedOutput(results),
            succeededCount,
            failedCount);
    }

    private static string BuildCombinedOutput(IReadOnlyList<AgentStepResult> results)
    {
        var sb = new StringBuilder();
        var first = true;

        foreach (var result in results)
        {
            if (!result.Succeeded)
                continue;

            if (!first)
                sb.AppendLine().AppendLine();

            sb.Append($"## {result.AgentName}");
            sb.AppendLine();
            sb.Append(result.Output);
            first = false;
        }

        return sb.ToString();
    }
}
