using Application.AI.Common.Interfaces.Planner;
using Domain.AI.Planner;
using Domain.Common;
using Domain.Common.Config;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.CQRS.Workflows.Submit;

/// <summary>
/// Handles <see cref="SubmitWorkflowCommand"/>: refuses when workflow submission is disabled, resolves
/// and bounds any referenced child workflows, maps the definition to a plan graph, and persists it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Submission stores; it does not run.</strong> The returned identifier names a stored
/// workflow, and starting one is a separately-authorized operation. A caller who can author a workflow
/// therefore does not automatically hold the right to spend the host's credentials executing it.
/// </para>
/// <para>
/// <strong>Ownership is stamped by the store from the caller's ambient scope</strong>, established at
/// the transport boundary before the request reached MediatR. This handler never reads or writes an
/// owner, because a handler that could would be a handler that could get it wrong — and in this
/// codebase an unscoped write is not a private record, it is a world-readable one.
/// </para>
/// </remarks>
public sealed class SubmitWorkflowCommandHandler
    : IRequestHandler<SubmitWorkflowCommand, Result<SubmitWorkflowResult>>
{
    private readonly IPlanStateStore _planStore;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<SubmitWorkflowCommandHandler> _logger;

    /// <summary>Initializes a new <see cref="SubmitWorkflowCommandHandler"/>.</summary>
    public SubmitWorkflowCommandHandler(
        IPlanStateStore planStore,
        IOptionsMonitor<AppConfig> config,
        ILogger<SubmitWorkflowCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(planStore);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _planStore = planStore;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<SubmitWorkflowResult>> Handle(
        SubmitWorkflowCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var submissionConfig = _config.CurrentValue.AI.WorkflowSubmission;
        if (!submissionConfig.Enabled)
        {
            return Result<SubmitWorkflowResult>.Forbidden(
                "Workflow submission is disabled. Set AppConfig.AI.WorkflowSubmission.Enabled = true to enable it.");
        }

        var nesting = await ValidateChildReferencesAsync(
            request.Definition, submissionConfig.MaxSubPlanNestingDepth, cancellationToken).ConfigureAwait(false);
        if (!nesting.IsSuccess)
            return Result<SubmitWorkflowResult>.ValidationFailure([.. nesting.Errors]);

        var mapped = WorkflowDefinitionMapper.MapToPlanGraph(request.Definition);
        if (!mapped.IsSuccess || mapped.Value is null)
            return Result<SubmitWorkflowResult>.ValidationFailure([.. mapped.Errors]);

        var plan = mapped.Value;
        var saved = await _planStore.SavePlanAsync(plan, cancellationToken).ConfigureAwait(false);
        if (!saved.IsSuccess)
        {
            _logger.LogError(
                "Failed to persist submitted workflow {WorkflowName} ({WorkflowId}): {Errors}",
                plan.Name, plan.Id.Value, string.Join("; ", saved.Errors));

            return Result<SubmitWorkflowResult>.Fail("The workflow could not be stored.");
        }

        _logger.LogInformation(
            "Stored submitted workflow {WorkflowName} ({WorkflowId}) with {StepCount} step(s) and {EdgeCount} edge(s).",
            plan.Name, plan.Id.Value, plan.Steps.Count, plan.Edges.Count);

        return Result<SubmitWorkflowResult>.Success(new SubmitWorkflowResult
        {
            WorkflowId = plan.Id.Value,
            Name = plan.Name,
            // Derived from the mapped plan rather than tracked alongside it: step names are unique
            // within a submission and are carried through unchanged, so the plan already holds the
            // mapping and a second copy could only ever disagree with it.
            StepIds = plan.Steps.ToDictionary(step => step.Name, step => step.Id.Value, StringComparer.Ordinal)
        });
    }

    /// <summary>
    /// Resolves every child workflow the definition references, confirming each is visible to the
    /// caller and that the resulting chain stays within the host's nesting cap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This runs at admission rather than being left to the executor, for two reasons. A dangling or
    /// unauthorized reference otherwise stores cleanly and fails only when run, which reports the
    /// defect to whoever ran the workflow rather than to whoever wrote it. And an over-deep chain
    /// otherwise trips <see cref="PlanConfiguration.MaxSubPlanDepth"/> mid-run, after the earlier
    /// steps have already spent real inference and tool calls.
    /// </para>
    /// <para>
    /// <see cref="IPlanStateStore.LoadPlanAsync"/> is scope-filtered, so a child belonging to another
    /// owner or tenant resolves to <see langword="null"/> — indistinguishable from one that does not
    /// exist. That is deliberate: the caller learns its reference is unusable without learning whether
    /// someone else's workflow carries that identifier.
    /// </para>
    /// </remarks>
    private async Task<Result> ValidateChildReferencesAsync(
        WorkflowDefinition definition, int maxDepth, CancellationToken cancellationToken)
    {
        var roots = definition.Steps
            .Select(step => step.Configuration)
            .OfType<SubPlanStepConfiguration>()
            .Select(configuration => new PlanId(configuration.ChildWorkflowId))
            .Distinct()
            .ToList();

        if (roots.Count == 0)
            return Result.Success();

        // The submitted definition occupies the first level, so its children may descend at most
        // maxDepth - 1 further before the chain exceeds the cap.
        var visited = new HashSet<PlanId>();
        var frontier = roots;

        for (var depth = 1; depth <= maxDepth && frontier.Count > 0; depth++)
        {
            var next = new List<PlanId>();
            foreach (var childId in frontier)
            {
                // A cycle is not an error here — PlanValidator owns structural validity. Skipping a
                // repeat visit only keeps this walk terminating and stops one plan being counted
                // twice toward the depth.
                if (!visited.Add(childId))
                    continue;

                var loaded = await _planStore.LoadPlanAsync(childId, cancellationToken).ConfigureAwait(false);
                if (!loaded.IsSuccess)
                    return Result.Fail("A referenced child workflow could not be read.");

                if (loaded.Value is null)
                {
                    return Result.Fail(
                        $"Child workflow '{childId.Value}' does not exist or is not available to this caller.");
                }

                next.AddRange(ChildReferencesOf(loaded.Value));
            }

            frontier = next;
        }

        if (frontier.Count > 0)
        {
            return Result.Fail(
                $"The referenced sub-workflow chain is deeper than the host's limit of {maxDepth}.");
        }

        return Result.Success();
    }

    private static IEnumerable<PlanId> ChildReferencesOf(PlanGraph plan) => plan.Steps
        .Select(step => step.Configuration)
        .OfType<SubPlanConfig>()
        .Select(configuration => configuration.ChildPlanId)
        .Where(childPlanId => childPlanId.HasValue)
        .Select(childPlanId => childPlanId!.Value);
}
