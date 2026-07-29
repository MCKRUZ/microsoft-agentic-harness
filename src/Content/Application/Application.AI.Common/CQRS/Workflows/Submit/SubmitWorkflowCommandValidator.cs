using Domain.AI.Planner;
using Domain.Common.Config.AI;
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

        RuleForEach(x => x.Definition.Steps).ChildRules(step =>
        {
            step.RuleFor(s => s.Name)
                .NotEmpty().WithMessage("Every step requires a name.")
                .Must(BeWithinStringCap).WithMessage(StringCapMessage);

            step.RuleFor(s => s.Configuration)
                .NotNull().WithMessage("Every step requires a configuration.")
                .Must((s, configuration) => DeclaredTypeMatches(s.Type, configuration))
                    .WithMessage(s => $"Step '{s.Name}' declares type {s.Type} but carries a configuration for a different type.");

            step.RuleFor(s => s.Timeout)
                .Must(timeout => timeout is null or { Ticks: > 0 })
                    .WithMessage(s => $"Step '{s.Name}' declares a non-positive timeout.");
        });

        RuleFor(x => x.Definition.Edges)
            .NotNull().WithMessage("An edge list is required; send an empty list for a single-step workflow.")
            .Must(edges => edges.Count <= Caps.MaxEdges)
                .WithMessage(_ => $"A workflow may declare at most {Caps.MaxEdges} edges.");

        // Cross-field rules: each needs both collections, so they hang off the definition rather than
        // off either one. Reported as one failure each so a caller fixing a definition sees every
        // distinct problem, not just the first.
        RuleFor(x => x.Definition)
            .Must(HaveResolvableEdgeEndpoints)
                .WithMessage("Every edge must name steps that exist in the same submission.")
            .Must(RespectFanOutCap)
                .WithMessage(_ => $"No step may have more than {Caps.MaxFanOutPerStep} outbound edges.")
            .When(x => x.Definition is { Steps: not null, Edges: not null });
    }

    private WorkflowSubmissionCaps Caps => new(_config.CurrentValue.WorkflowSubmission);

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
    /// Snapshot of the caps for one validation pass, so every rule in that pass sees the same values
    /// even if configuration is reloaded mid-validation.
    /// </summary>
    private readonly record struct WorkflowSubmissionCaps
    {
        public WorkflowSubmissionCaps(Domain.Common.Config.AI.WorkflowSubmission.WorkflowSubmissionConfig source)
        {
            MaxSteps = source.MaxSteps;
            MaxEdges = source.MaxEdges;
            MaxFanOutPerStep = source.MaxFanOutPerStep;
            MaxStringFieldLength = source.MaxStringFieldLength;
        }

        public int MaxSteps { get; }
        public int MaxEdges { get; }
        public int MaxFanOutPerStep { get; }
        public int MaxStringFieldLength { get; }
    }
}
