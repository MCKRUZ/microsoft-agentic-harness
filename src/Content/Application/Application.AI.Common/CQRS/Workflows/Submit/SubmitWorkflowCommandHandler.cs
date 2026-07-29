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
    private readonly IPlanValidator _validator;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<SubmitWorkflowCommandHandler> _logger;

    /// <summary>Initializes a new <see cref="SubmitWorkflowCommandHandler"/>.</summary>
    public SubmitWorkflowCommandHandler(
        IPlanStateStore planStore,
        IPlanValidator validator,
        IOptionsMonitor<AppConfig> config,
        ILogger<SubmitWorkflowCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(planStore);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _planStore = planStore;
        _validator = validator;
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
        {
            // The walk distinguishes a bad submission from a store fault, and that distinction has to
            // survive: a caller told its request is malformed will not retry a transient read failure.
            return nesting.FailureType == ResultFailureType.Validation
                ? Result<SubmitWorkflowResult>.ValidationFailure([.. nesting.Errors])
                : Result<SubmitWorkflowResult>.Fail([.. nesting.Errors]);
        }

        var mapped = WorkflowDefinitionMapper.MapToPlanGraph(request.Definition);
        if (!mapped.IsSuccess || mapped.Value is null)
            return Result<SubmitWorkflowResult>.ValidationFailure([.. mapped.Errors]);

        var plan = mapped.Value;

        // Structural validation — cycles, reachability, branch completeness — via the same
        // IPlanValidator the executor runs, on the mapped graph. The wire contract deliberately does
        // not re-implement any of it, which only holds if it actually runs here: without this call a
        // submission whose steps form a cycle passes every admission rule, is stored, and fails on
        // first execution. That reports the defect to whoever ran the workflow rather than to whoever
        // wrote it, which is the whole thing admission exists to prevent.
        var structure = await _validator.ValidateAsync(plan, cancellationToken).ConfigureAwait(false);

        // IPlanValidator reports a structurally invalid plan as a *failed* Result of validation type —
        // not as a successful result carrying IsValid = false. Reading it the other way round turns
        // every rejected plan into a 500. The IsValid check below still runs because the interface
        // permits that shape and an alternative implementation may use it.
        if (!structure.IsSuccess)
        {
            if (structure.FailureType == ResultFailureType.Validation)
                return Result<SubmitWorkflowResult>.ValidationFailure([.. structure.Errors]);

            _logger.LogError(
                "Structural validation could not be completed for submitted workflow {WorkflowName}: {Errors}",
                plan.Name, string.Join("; ", structure.Errors));

            return Result<SubmitWorkflowResult>.Fail("The workflow could not be validated.");
        }

        if (structure.Value is { IsValid: false } invalid)
            return Result<SubmitWorkflowResult>.ValidationFailure([.. invalid.Errors]);

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

        var context = new ChildWalkContext(maxDepth);
        foreach (var rootId in roots)
        {
            var outcome = await MeasureChainAsync(rootId, depth: 1, context, cancellationToken)
                .ConfigureAwait(false);
            if (!outcome.IsSuccess)
                return outcome;
        }

        return Result.Success();
    }

    /// <summary>
    /// Walks one child reference and everything beneath it, failing when the chain outgrows the cap,
    /// references itself, or names a workflow this caller cannot see.
    /// </summary>
    /// <remarks>
    /// Depth is carried down the path rather than tracked in a shared visited set. A shared set
    /// under-counts: a plan first reached at depth 1 is marked seen, and when the same plan is reached
    /// again at depth 3 through a longer route its subtree is not re-expanded, so a chain that exceeds
    /// the cap can be admitted. Measuring per path is what makes the reported depth the real one.
    /// Recursion is bounded by the cap itself — the walk stops the moment it exceeds it — and
    /// <see cref="ChildWalkContext.Path"/> catches a reference cycle, which would otherwise recurse
    /// until that bound on every submission that contains one.
    /// </remarks>
    private async Task<Result> MeasureChainAsync(
        PlanId childId, int depth, ChildWalkContext context, CancellationToken cancellationToken)
    {
        if (depth > context.MaxDepth)
        {
            return Result.Fail(
                $"The referenced sub-workflow chain is deeper than the host's limit of {context.MaxDepth}.");
        }

        if (!context.Path.Add(childId))
            return Result.Fail($"Child workflow '{childId.Value}' takes part in a sub-workflow cycle.");

        try
        {
            var loaded = await _planStore.LoadPlanAsync(childId, cancellationToken).ConfigureAwait(false);
            if (!loaded.IsSuccess)
            {
                // A store fault, not a malformed submission. Reporting it as a validation failure would
                // tell the caller its request was wrong and stop it retrying something that would work.
                _logger.LogError(
                    "Could not read child workflow {ChildWorkflowId} during admission: {Errors}",
                    childId.Value, string.Join("; ", loaded.Errors));

                return Result.Fail("A referenced child workflow could not be read.");
            }

            if (loaded.Value is null)
            {
                return Result.ValidationFailure(
                    [$"Child workflow '{childId.Value}' does not exist or is not available to this caller."]);
            }

            foreach (var grandchildId in ChildReferencesOf(loaded.Value))
            {
                var outcome = await MeasureChainAsync(grandchildId, depth + 1, context, cancellationToken)
                    .ConfigureAwait(false);
                if (!outcome.IsSuccess)
                    return outcome;
            }

            return Result.Success();
        }
        finally
        {
            context.Path.Remove(childId);
        }
    }

    /// <summary>State carried through one submission's child-reference walk.</summary>
    /// <param name="MaxDepth">The host's admission cap on chain depth.</param>
    private sealed record ChildWalkContext(int MaxDepth)
    {
        /// <summary>The plans on the path currently being measured, used to detect a reference cycle.</summary>
        public HashSet<PlanId> Path { get; } = [];
    }

    private static IEnumerable<PlanId> ChildReferencesOf(PlanGraph plan) => plan.Steps
        .Select(step => step.Configuration)
        .OfType<SubPlanConfig>()
        .Select(configuration => configuration.ChildPlanId)
        .Where(childPlanId => childPlanId.HasValue)
        .Select(childPlanId => childPlanId!.Value);
}
