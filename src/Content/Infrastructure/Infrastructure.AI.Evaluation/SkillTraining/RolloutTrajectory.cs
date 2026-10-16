using System.Text;
using Application.AI.Common.Evaluation.Models;
using Domain.AI.Evaluation;

namespace Infrastructure.AI.Evaluation.SkillTraining;

/// <summary>
/// Renders what an agent did on one eval case as the text the skill-training proposer reads (#843).
/// </summary>
/// <remarks>
/// <para>
/// <b>What it contains:</b> the task as the agent received it, every tool call with its arguments and
/// result in call order, and the final answer.
/// </para>
/// <para>
/// <b>What it deliberately leaves out:</b> the case's expected output and metric specs (an answer key
/// the proposer must never see), its tags, and the invocation's error text. A failed run is said to have
/// ended in an error and nothing more: the detail of a provider failure can quote URLs and headers, and
/// the proposer needs to know the run did not finish, not why a vendor refused it.
/// </para>
/// </remarks>
internal static class RolloutTrajectory
{
    /// <summary>Renders the trajectory for <paramref name="case"/> and its <paramref name="invocation"/>.</summary>
    /// <param name="case">The case that was run.</param>
    /// <param name="invocation">What the invoker returned for it.</param>
    public static string Render(EvalCase @case, AgentInvocationResult invocation)
    {
        ArgumentNullException.ThrowIfNull(@case);
        ArgumentNullException.ThrowIfNull(invocation);

        var text = new StringBuilder();
        text.Append("# TASK\n").Append(@case.Input).Append("\n\n");

        if (invocation.ToolCalls.Count > 0)
        {
            text.Append("# WHAT THE AGENT DID\n\n");
            for (var i = 0; i < invocation.ToolCalls.Count; i++)
            {
                var call = invocation.ToolCalls[i];
                text.Append("## Step ").Append(i + 1).Append(": ").Append(call.ToolName).Append('\n')
                    .Append("arguments: ").Append(call.Input ?? "(none)").Append('\n')
                    .Append("result: ").Append(call.Output ?? "(no result recorded)").Append("\n\n");
            }
        }
        else if (invocation.ToolsInvoked.Count > 0)
        {
            text.Append("# TOOLS THE AGENT CALLED\n").Append(string.Join(", ", invocation.ToolsInvoked)).Append("\n\n");
        }

        if (invocation.Success)
            text.Append("# FINAL ANSWER\n").Append(invocation.Output);
        else
            text.Append("# THE RUN DID NOT FINISH\nIt ended in an error before the agent gave a final answer.");

        return text.ToString();
    }
}
