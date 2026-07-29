using Domain.AI.Planner;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.WorkflowSubmission;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.CQRS.Workflows.Submit;

/// <summary>
/// Admission validation for <see cref="SubmitWorkflowCommand"/>: bounds the submission against the
/// host's configured caps and checks the wire-level integrity the domain model cannot express.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This does not re-implement <c>PlanValidator</c>.</strong> Cycles, reachability and branch
/// completeness are enforced there, on the built <c>PlanGraph</c>, via Kahn's algorithm; the
/// submission path runs that same validation, so duplicating it here would create two definitions of
/// "valid" that can drift apart. What this adds is the two things <c>PlanValidator</c> cannot see: the
/// host's admission caps, and the name-based references that exist only on the wire and are resolved
/// away by the mapper.
/// </para>
/// <para>
/// <strong>Rejection, not clamping.</strong> A submission exceeding a cap is refused. Silently
/// lowering a caller's requested timeout or truncating their prompt would produce a run that differs
/// from the one they authored, and they would discover the difference from its behaviour in
/// production rather than from a 400 at submission time.
/// </para>
/// <para>
/// Every rule reads the live configuration through <see cref="IOptionsMonitor{TOptions}"/>, so a cap
/// change takes effect without a restart. A reload landing between a check and the message that
/// reports it could therefore quote a cap microseconds newer than the one enforced; that is cosmetic,
/// and the alternative — freezing the caps at construction — would mean a tightened limit went on
/// admitting oversized submissions until the host was restarted.
/// </para>
/// </remarks>
public sealed class SubmitWorkflowCommandValidator : AbstractValidator<SubmitWorkflowCommand>
{
    private readonly IOptionsMonitor<AIConfig> _config;

    /// <summary>Initializes validation rules against the host's current submission caps.</summary>
    /// <param name="config">Live view of the AI configuration, so a cap change takes effect without a restart.</param>
    public SubmitWorkflowCommandValidator(IOptionsMonitor<AIConfig> config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;

        RuleFor(x => x.Definition)
            .NotNull().WithMessage("A workflow definition is required.");

        RuleFor(x => x.Definition.Name)
            .NotEmpty().WithMessage("The workflow requires a name.")
            .Must(BeWithinStringCap).WithMessage(StringCapMessage);

        RuleFor(x => x.Definition.Steps)
            .NotEmpty().WithMessage("A workflow requires at least one step.")
            .Must(steps => steps.Count <= Caps.MaxSteps)
                .WithMessage(_ => $"A workflow may declare at most {Caps.MaxSteps} steps.")
            .Must(HaveUniqueNames)
                .WithMessage("Step names must be unique within a submission; edges refer to steps by name.");

        RuleFor(x => x.Definition.Edges)
            .NotNull().WithMessage("An edge list is required; send an empty list for a single-step workflow.")
            .Must(edges => edges.Count <= Caps.MaxEdges)
                .WithMessage(_ => $"A workflow may declare at most {Caps.MaxEdges} edges.")
            .Must(edges => edges.All(e => BeWithinStringCap(e.Condition))).WithMessage(StringCapMessage);

        RuleFor(x => x.Definition.Configuration!)
            .Must(RespectExecutionCeilings)
                .WithMessage(_ => "Requested execution settings exceed the host's ceilings: a workflow may "
                    + $"run for at most {Caps.MaxPlanTimeout} with at most {Caps.MaxParallelSteps} steps in parallel.")
            .When(x => x.Definition?.Configuration is not null);

        RuleForEach(x => x.Definition.Steps).ChildRules(ConfigureStepRules);

        // Cross-field rules: each needs both collections, so they hang off the definition rather than
        // off either one. Reported as one failure each so a caller fixing a definition sees every
        // distinct problem, not just the first.
        RuleFor(x => x.Definition)
            .Must(HaveResolvableEdgeEndpoints)
                .WithMessage("Every edge must name steps that exist in the same submission.")
            .Must(RespectFanOutCap)
                .WithMessage(_ => $"No step may have more than {Caps.MaxFanOutPerStep} outbound edges.")
            .Must(HaveUnambiguousConditionalBranches)
                .WithMessage("Every ConditionalBranch step requires exactly one outgoing ConditionalTrue edge "
                    + "and exactly one outgoing ConditionalFalse edge.")
            .When(x => x.Definition is { Steps: not null, Edges: not null });
    }

    private WorkflowSubmissionConfig Caps => _config.CurrentValue.WorkflowSubmission;

    private void ConfigureStepRules(InlineValidator<WorkflowStep> step)
    {
        step.RuleFor(s => s.Name)
            .NotEmpty().WithMessage("Every step requires a name.")
            .Must(BeWithinStringCap).WithMessage(StringCapMessage);

        step.RuleFor(s => s.Configuration)
            .NotNull().WithMessage("Every step requires a configuration.")
            .Must((s, configuration) => DeclaredTypeMatches(s.Type, configuration))
                .WithMessage(s => $"Step '{s.Name}' declares type {s.Type} but carries a configuration for a different type.")
            .Must(RespectConfigurationStringCaps).WithMessage(StringCapMessage)
            .Must(RespectHumanGateTimeout)
                .WithMessage(s => $"Step '{s.Name}' may park awaiting approval for at most {Caps.MaxHumanGateTimeout}.");

        step.RuleFor(s => s.Timeout)
            .Must(timeout => timeout is null or { Ticks: > 0 })
                .WithMessage(s => $"Step '{s.Name}' declares a non-positive timeout.")
            .Must(timeout => timeout is null || timeout <= Caps.MaxStepTimeout)
                .WithMessage(s => $"Step '{s.Name}' requests a timeout above the host's ceiling of {Caps.MaxStepTimeout}.");

        step.RuleFor(s => s.Retry!)
            .Must(RespectRetryCeilings)
                .WithMessage(s => $"Step '{s.Name}' requests retry settings outside the host's limits: at most "
                    + $"{Caps.MaxRetriesPerStep} retries, with a non-negative initial delay.")
            .When(s => s.Retry is not null);
    }

    private static string StringCapMessage(object _) => "A caller-supplied string field exceeds the host's length cap.";

    private bool BeWithinStringCap(string? value) =>
        value is null || value.Length <= Caps.MaxStringFieldLength;

    private static bool HaveUniqueNames(IReadOnlyList<WorkflowStep> steps) =>
        steps.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count() == steps.Count;

    /// <summary>
    /// Confirms the step's declared <see cref="StepType"/> agrees with the polymorphic configuration
    /// actually supplied.
    /// </summary>
    /// <remarks>
    /// The JSON discriminator and the <c>Type</c> property are two statements of the same fact, and a
    /// submission can make them disagree. Rejecting is the only safe answer: preferring the
    /// discriminator would let a caller label a step <c>HumanGate</c> while shipping a
    /// <c>ToolUse</c> body, and preferring <c>Type</c> would resolve an executor that cannot read the
    /// configuration it is handed.
    /// </remarks>
    private static bool DeclaredTypeMatches(StepType declared, WorkflowStepConfiguration? configuration) =>
        configuration is not null && configuration.StepType == declared;

    /// <summary>
    /// Applies the string-length cap to the free-text a step configuration carries. These fields —
    /// prompts, queries, escalation messages, condition expressions — are the ones a caller can make
    /// arbitrarily large, and every one of them is either persisted or sent to a model.
    /// </summary>
    private bool RespectConfigurationStringCaps(WorkflowStepConfiguration? configuration) => configuration switch
    {
        LlmCallStepConfiguration c => BeWithinStringCap(c.SystemPrompt) && BeWithinStringCap(c.ModelDeploymentKey),
        ToolUseStepConfiguration c => BeWithinStringCap(c.ToolName),
        HumanGateStepConfiguration c => BeWithinStringCap(c.EscalationMessage)
            && c.Approvers.All(BeWithinStringCap),
        ConditionalBranchStepConfiguration c => BeWithinStringCap(c.ConditionExpression),
        RetrievalWorkflowStepConfiguration c => BeWithinStringCap(c.Query),
        _ => true
    };

    private bool RespectHumanGateTimeout(WorkflowStepConfiguration? configuration) =>
        configuration is not HumanGateStepConfiguration gate
        || gate.Timeout is null
        || (gate.Timeout > TimeSpan.Zero && gate.Timeout <= Caps.MaxHumanGateTimeout);

    private bool RespectExecutionCeilings(WorkflowExecutionSettings settings)
    {
        var caps = Caps;
        var timeoutOk = settings.PlanTimeout is null
            || (settings.PlanTimeout > TimeSpan.Zero && settings.PlanTimeout <= caps.MaxPlanTimeout);
        var parallelOk = settings.MaxParallelSteps is null
            || (settings.MaxParallelSteps >= 1 && settings.MaxParallelSteps <= caps.MaxParallelSteps);

        return timeoutOk && parallelOk;
    }

    private bool RespectRetryCeilings(WorkflowRetrySettings retry)
    {
        var retriesOk = retry.MaxRetries is null
            || (retry.MaxRetries >= 0 && retry.MaxRetries <= Caps.MaxRetriesPerStep);
        var delayOk = retry.InitialDelay is null || retry.InitialDelay >= TimeSpan.Zero;

        return retriesOk && delayOk;
    }

    private static bool HaveResolvableEdgeEndpoints(WorkflowDefinition definition)
    {
        var names = definition.Steps.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        return definition.Edges.All(e => names.Contains(e.From) && names.Contains(e.To));
    }

    private bool RespectFanOutCap(WorkflowDefinition definition) =>
        definition.Edges
            .GroupBy(e => e.From, StringComparer.Ordinal)
            .All(group => group.Count() <= Caps.MaxFanOutPerStep);

    /// <summary>
    /// Requires each conditional step to declare exactly one true arm and exactly one false arm.
    /// </summary>
    /// <remarks>
    /// <c>PlanValidator</c> checks that both arms are <em>present</em>, which is branch completeness.
    /// This checks that neither is present <em>twice</em>, which it does not — and two edges labelled
    /// the same way are two answers to one question, so something downstream would have to pick one
    /// silently. Rejecting here is also what lets <c>WorkflowDefinitionMapper</c> resolve the branch
    /// targets at all: <c>ConditionalBranchConfig</c> declares both as required properties.
    /// </remarks>
    private static bool HaveUnambiguousConditionalBranches(WorkflowDefinition definition)
    {
        var conditionalSteps = definition.Steps
            .Where(s => s.Type == StepType.ConditionalBranch)
            .Select(s => s.Name);

        return conditionalSteps.All(name =>
        {
            var outgoing = definition.Edges.Where(e => string.Equals(e.From, name, StringComparison.Ordinal)).ToList();
            return outgoing.Count(e => e.Type == EdgeType.ConditionalTrue) == 1
                && outgoing.Count(e => e.Type == EdgeType.ConditionalFalse) == 1;
        });
    }
}
