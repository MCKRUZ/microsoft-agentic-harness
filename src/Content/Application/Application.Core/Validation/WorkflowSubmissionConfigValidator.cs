using Domain.Common.Config.AI.WorkflowSubmission;
using FluentValidation;

namespace Application.Core.Validation;

/// <summary>
/// Validates <see cref="WorkflowSubmissionConfig"/>, asserting that every admission cap and ceiling
/// the section declares is a usable bound.
/// </summary>
/// <remarks>
/// <para>
/// Every rule is unconditional (not gated on <see cref="WorkflowSubmissionConfig.Enabled"/>): the class
/// defaults are all valid, so a host that omits the section — or leaves the subsystem off — binds
/// defaults and boots unchanged. A rule only bites when an operator supplies an explicit bad value,
/// which is a misconfiguration whether or not the feature is switched on.
/// </para>
/// <para>
/// The failure this closes is a caps section that reads as protective and rejects everything. A
/// <c>MaxSteps</c> of zero refuses every workflow, including the single-step one an operator writes to
/// check the endpoint works, and the response says only that the submission exceeded a cap — leaving
/// the operator to conclude the feature is broken rather than that they mistyped a limit. The same
/// shape applies to each bound here. Failing closed at startup names the setting instead.
/// </para>
/// </remarks>
public sealed class WorkflowSubmissionConfigValidator : AbstractValidator<WorkflowSubmissionConfig>
{
    /// <summary>Initializes the rule set. Every quantity is strictly positive.</summary>
    public WorkflowSubmissionConfigValidator()
    {
        RuleFor(x => x.MaxRequestBytes)
            .GreaterThan(0)
            .WithMessage("MaxRequestBytes must be > 0 — a non-positive limit would reject every submission.");

        RuleFor(x => x.MaxSteps)
            .GreaterThan(0)
            .WithMessage("MaxSteps must be > 0 — a non-positive limit would reject every workflow, since a workflow requires at least one step.");

        RuleFor(x => x.MaxEdges)
            .GreaterThan(0)
            .WithMessage("MaxEdges must be > 0 — a non-positive limit would reject every workflow that connects two steps.");

        RuleFor(x => x.MaxFanOutPerStep)
            .GreaterThan(0)
            .WithMessage("MaxFanOutPerStep must be > 0 — a non-positive limit would reject every workflow with an edge.");

        RuleFor(x => x.MaxSubPlanNestingDepth)
            .GreaterThan(0)
            .WithMessage("MaxSubPlanNestingDepth must be > 0 — a non-positive depth would reject every workflow that references a child.");

        RuleFor(x => x.MaxStringFieldLength)
            .GreaterThan(0)
            .WithMessage("MaxStringFieldLength must be > 0 — a non-positive length would reject every named step.");

        RuleFor(x => x.MaxPlanTimeout)
            .GreaterThan(TimeSpan.Zero)
            .WithMessage("MaxPlanTimeout must be > 0 — a non-positive ceiling would reject every requested plan timeout.");

        RuleFor(x => x.MaxStepTimeout)
            .GreaterThan(TimeSpan.Zero)
            .WithMessage("MaxStepTimeout must be > 0 — a non-positive ceiling would reject every requested step timeout.");

        RuleFor(x => x.MaxParallelSteps)
            .GreaterThan(0)
            .WithMessage("MaxParallelSteps must be > 0 — a non-positive ceiling would reject every requested parallelism.");

        RuleFor(x => x.MaxRetriesPerStep)
            .GreaterThanOrEqualTo(0)
            .WithMessage("MaxRetriesPerStep must be >= 0 — zero is meaningful (no retries permitted), negative is not.");

        RuleFor(x => x.MaxTokensPerStep)
            .GreaterThan(0)
            .WithMessage("MaxTokensPerStep must be > 0 — a non-positive ceiling would reject every requested completion length.");

        RuleFor(x => x.MaxTopK)
            .GreaterThan(0)
            .WithMessage("MaxTopK must be > 0 — a non-positive ceiling would reject every retrieval step that asks for results.");

        RuleFor(x => x.MaxStoredWorkflowsPerOwner)
            .GreaterThan(0)
            .WithMessage("MaxStoredWorkflowsPerOwner must be > 0 — a non-positive quota would reject every caller's first submission.");

        RuleFor(x => x.RunRecordTtl)
            .GreaterThan(TimeSpan.Zero)
            .WithMessage("RunRecordTtl must be > 0 — a non-positive retention reclaims every run the moment it finishes, so no caller could ever read an outcome.");

        RuleFor(x => x.MaxConcurrentRunsPerOwner)
            .GreaterThan(0)
            .WithMessage("MaxConcurrentRunsPerOwner must be > 0 — a non-positive limit would refuse every caller's first run.");

        RuleFor(x => x.RunSweepInterval)
            .GreaterThan(TimeSpan.Zero)
            .WithMessage("RunSweepInterval must be > 0 — a non-positive interval would spin the sweeper continuously instead of scheduling it.");

        RuleFor(x => x.MaxHumanGateTimeout)
            .GreaterThan(TimeSpan.Zero)
            .WithMessage("MaxHumanGateTimeout must be > 0 — a non-positive ceiling would reject every requested gate timeout.");
    }
}
