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
            .Must(ContainNoNullElements).WithMessage("A workflow may not contain a null step.")
            .Must(steps => steps.Count <= Caps.MaxSteps)
                .WithMessage(_ => $"A workflow may declare at most {Caps.MaxSteps} steps.")
            .Must(HaveUniqueNames)
                .WithMessage("Step names must be unique within a submission; edges refer to steps by name.");

        RuleFor(x => x.Definition.Edges)
            .NotNull().WithMessage("An edge list is required; send an empty list for a single-step workflow.")
            .Must(ContainNoNullElements).WithMessage("A workflow may not contain a null edge.")
            .Must(edges => edges.Count <= Caps.MaxEdges)
                .WithMessage(_ => $"A workflow may declare at most {Caps.MaxEdges} edges.")
            .Must(edges => edges.All(e => e is null || BeWithinStringCap(e.Condition)))
                .WithMessage(StringCapMessage);

        RuleFor(x => x.Definition.Configuration!)
            .Must(RespectExecutionCeilings)
                .WithMessage(_ => "Requested execution settings exceed the host's ceilings: a workflow may "
                    + $"run for at most {Caps.MaxPlanTimeout} with at most {Caps.MaxParallelSteps} steps in parallel.")
            .When(x => x.Definition?.Configuration is not null);

        RuleForEach(x => x.Definition.Steps).Where(step => step is not null).ChildRules(ConfigureStepRules);

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
                .WithMessage(s => $"Step '{s.Name}' may park awaiting approval for at most {Caps.MaxHumanGateTimeout}.")
            .Must(CarryRequiredContent)
                .WithMessage(s => $"Step '{s.Name}' omits content its type requires — a prompt, tool name, query, "
                    + "condition, or an escalation message with at least one approver.")
            .Must(RespectPerStepCostCeilings)
                .WithMessage(s => $"Step '{s.Name}' requests more than the host permits: at most "
                    + $"{Caps.MaxTokensPerStep} response tokens and {Caps.MaxTopK} retrieval results.")
            .Must(NameAPermittedDeployment)
                .WithMessage(s => $"Step '{s.Name}' names a model deployment this host does not offer.")
            .Must(CarryAnEvaluableCondition)
                .WithMessage(s => $"Step '{s.Name}' declares a condition the branch evaluator will refuse: at most "
                    + $"{ConditionExpressionRules.MaxLength} characters, no member access, and only comparison and "
                    + "boolean operators over upstream step names.");

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

    /// <summary>
    /// Whether a caller-supplied collection contains no null entries.
    /// </summary>
    /// <remarks>
    /// This is load-bearing, not defensive dressing. MVC's implicit-required rejects <c>"steps": null</c>
    /// but says nothing about <c>"steps": [null]</c> — element nullability is not enforced by model
    /// binding — and FluentValidation's default cascade keeps evaluating later rules after an earlier
    /// one fails. Without this the first predicate to touch the element throws, and a malformed body
    /// becomes a 500 from the component whose entire job is to reject malformed bodies with a 400.
    /// Every predicate below therefore also tolerates a null entry rather than relying on rule order.
    /// </remarks>
    private static bool ContainNoNullElements<T>(IReadOnlyList<T>? items) where T : class =>
        items is null || items.All(item => item is not null);

    private static bool HaveUniqueNames(IReadOnlyList<WorkflowStep> steps) =>
        !ContainNoNullElements(steps)
        || steps.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count() == steps.Count;

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
        // Argument values as well as the tool name: the config documents this cap as covering "tool
        // arguments", and they are the part of a tool step a caller can actually make enormous.
        ToolUseStepConfiguration c => BeWithinStringCap(c.ToolName)
            && c.InputParameters.Values.OfType<string>().All(BeWithinStringCap),
        HumanGateStepConfiguration c => BeWithinStringCap(c.EscalationMessage)
            && c.Approvers.All(BeWithinStringCap),
        ConditionalBranchStepConfiguration c => BeWithinStringCap(c.ConditionExpression),
        RetrievalWorkflowStepConfiguration c => BeWithinStringCap(c.Query),
        _ => true
    };

    /// <summary>
    /// Holds a submitted condition expression to the same rule the branch executor applies.
    /// </summary>
    /// <remarks>
    /// Without this, an expression the executor will refuse is admitted and stored: the workflow looks
    /// healthy, and the first run fails at the branch with a rejection the author never saw. Both sides
    /// call <see cref="ConditionExpressionRules"/> so there is one definition rather than two that can
    /// drift apart.
    /// </remarks>
    private static bool CarryAnEvaluableCondition(WorkflowStepConfiguration? configuration) =>
        configuration is not ConditionalBranchStepConfiguration branch
        || ConditionExpressionRules.IsSafe(branch.ConditionExpression);

    /// <summary>
    /// Requires each step to carry the content its type cannot function without.
    /// </summary>
    /// <remarks>
    /// A human gate with no approvers is the case worth naming: it is admitted happily, parks the run,
    /// and can never be answered by anyone, so it occupies a slot until it times out. An empty prompt,
    /// tool name, query, or condition fails sooner and louder, but all of them describe a step that
    /// cannot do the thing it names.
    /// </remarks>
    private static bool CarryRequiredContent(WorkflowStepConfiguration? configuration) => configuration switch
    {
        LlmCallStepConfiguration c => !string.IsNullOrWhiteSpace(c.SystemPrompt)
            && !string.IsNullOrWhiteSpace(c.ModelDeploymentKey),
        ToolUseStepConfiguration c => !string.IsNullOrWhiteSpace(c.ToolName),
        HumanGateStepConfiguration c => !string.IsNullOrWhiteSpace(c.EscalationMessage)
            && c.Approvers.Count > 0
            && c.Approvers.All(approver => !string.IsNullOrWhiteSpace(approver)),
        ConditionalBranchStepConfiguration c => !string.IsNullOrWhiteSpace(c.ConditionExpression),
        RetrievalWorkflowStepConfiguration c => !string.IsNullOrWhiteSpace(c.Query),
        _ => true
    };

    /// <summary>
    /// Applies the per-step spend ceilings. Response tokens and retrieval breadth are the two
    /// quantities a single step can inflate without touching any graph-size cap.
    /// </summary>
    private bool RespectPerStepCostCeilings(WorkflowStepConfiguration? configuration) => configuration switch
    {
        LlmCallStepConfiguration c => c.MaxTokens is null
            || (c.MaxTokens >= 1 && c.MaxTokens <= Caps.MaxTokensPerStep),
        RetrievalWorkflowStepConfiguration c => c.TopK is null
            || (c.TopK >= 1 && c.TopK <= Caps.MaxTopK),
        _ => true
    };

    /// <summary>
    /// Confirms an LLM step names a deployment the host has declared.
    /// </summary>
    /// <remarks>
    /// When <c>AgentFramework.AvailableDeployments</c> is empty the host has declared no allow-list, and
    /// the key passes through to be resolved at run time. That is a deliberate pass-through, not a
    /// fail-open on identity: an unrecognised deployment key fails the step that used it and grants the
    /// caller nothing. A host that wants submitted workflows confined to specific models states them.
    /// </remarks>
    private bool NameAPermittedDeployment(WorkflowStepConfiguration? configuration)
    {
        if (configuration is not LlmCallStepConfiguration llmCall)
            return true;

        var permitted = _config.CurrentValue.AgentFramework.AvailableDeployments;
        return permitted.Count == 0
            || permitted.Contains(llmCall.ModelDeploymentKey, StringComparer.OrdinalIgnoreCase);
    }

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
        if (!ContainNoNullElements(definition.Steps) || !ContainNoNullElements(definition.Edges))
            return true; // reported by the null-element rules; do not also throw here

        var names = definition.Steps.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        return definition.Edges.All(e => names.Contains(e.From) && names.Contains(e.To));
    }

    private bool RespectFanOutCap(WorkflowDefinition definition) =>
        !ContainNoNullElements(definition.Edges)
        || definition.Edges
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
        if (!ContainNoNullElements(definition.Steps) || !ContainNoNullElements(definition.Edges))
            return true; // reported by the null-element rules; do not also throw here

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
