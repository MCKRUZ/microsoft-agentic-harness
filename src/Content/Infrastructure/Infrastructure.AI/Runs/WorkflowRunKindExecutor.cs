using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Interfaces.Runs;
using Domain.AI.Planner;
using Domain.AI.Runs;
using Domain.Common;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Runs;

/// <summary>
/// Runs a stored workflow for the shared run substrate, delegating to <see cref="IPlanRunExecutor"/>.
/// </summary>
/// <remarks>
/// Deliberately thin. Every hard part of running a plan safely — the fresh service scope, arming the
/// caller's capability envelope and governance identity inside it, the conversation budget, and
/// returning stable scrubbed error codes rather than exception text — already lives in
/// <see cref="IPlanRunExecutor"/>. This translates one shape into another and nothing else, which is
/// what "a new run kind is a registration" is supposed to mean.
/// </remarks>
public sealed class WorkflowRunKindExecutor : IRunKindExecutor
{
    private readonly IPlanRunExecutor _planRunExecutor;
    private readonly ILogger<WorkflowRunKindExecutor> _logger;

    /// <summary>Initializes the executor.</summary>
    public WorkflowRunKindExecutor(
        IPlanRunExecutor planRunExecutor, ILogger<WorkflowRunKindExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(planRunExecutor);
        ArgumentNullException.ThrowIfNull(logger);

        _planRunExecutor = planRunExecutor;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<RunCompletion>> ExecuteAsync(RunRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (!Guid.TryParse(record.TargetId, out var workflowId))
        {
            // Unreachable through the HTTP surface, which parses the id before accepting the run. It
            // is still answered rather than thrown, because a malformed target must fail this one run
            // instead of surfacing as an unexpected dispatcher exception.
            _logger.LogError("Run {JobId} names a target that is not a workflow id.", record.JobId);
            return Result<RunCompletion>.Fail("The run names a workflow that cannot be resolved.");
        }

        var outcome = await _planRunExecutor.ExecuteAsync(
            new PlanRunRequest
            {
                PlanId = new PlanId(workflowId),
                Envelope = record.Envelope,

                // The workflow itself is the audit subject, not the caller. The caller is already
                // recorded on the run and is what the envelope was resolved from, and an owner id
                // comes from token text that need not satisfy the identifier charset this field
                // requires — deriving from the server-minted workflow id always does.
                AgentId = $"workflow:{workflowId}",

                // Null lets the run scope derive from the plan id, which is what bounds the run-level
                // conversation budget.
                ConversationId = null
            },
            cancellationToken).ConfigureAwait(false);

        if (!outcome.IsSuccess)
            return Result<RunCompletion>.Fail([.. outcome.Errors]);

        var summary = outcome.Value;
        if (summary is null)
            return Result<RunCompletion>.Fail("The run produced no execution summary.");

        return Result<RunCompletion>.Success(Interpret(summary.FinalStatus));
    }

    /// <summary>
    /// Translates the plan's final status into how the run ended.
    /// </summary>
    /// <remarks>
    /// Written as "succeed only on <see cref="StepExecutionStatus.Completed"/>", matching
    /// <c>SubPlanStepExecutor</c>, rather than "fail only on <see cref="StepExecutionStatus.Failed"/>".
    /// The two read the same until you notice that a plan can also end Blocked or Cancelled — under
    /// the inverted form both fall through to success, and a workflow parked awaiting an approval
    /// reports to its caller as finished work.
    /// </remarks>
    private static RunCompletion Interpret(StepExecutionStatus finalStatus) => finalStatus switch
    {
        StepExecutionStatus.Completed => RunCompletion.Succeeded(),

        StepExecutionStatus.Blocked => RunCompletion.Blocked(
            "The workflow is waiting on an approval before it can continue."),

        StepExecutionStatus.Cancelled => RunCompletion.Cancelled(
            "The workflow was cancelled before it finished."),

        _ => RunCompletion.Failed($"The workflow ended with status {finalStatus}.")
    };
}
