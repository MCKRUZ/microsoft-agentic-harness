using Domain.AI.Planner;
using Domain.Common;

namespace Application.AI.Common.CQRS.Workflows.Submit;

/// <summary>
/// Translates a caller-submitted <see cref="WorkflowDefinition"/> into the executable
/// <see cref="PlanGraph"/> the planner runs.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every identifier is minted here.</strong> A caller names its steps with arbitrary strings
/// and refers to them by those names; this mapper assigns the <see cref="PlanStepId"/> and
/// <see cref="PlanId"/> values that are actually persisted. Nothing the caller sends becomes an
/// identifier, so one submission cannot collide with — or deliberately target — another's.
/// </para>
/// <para>
/// <strong>Requested settings fall back to the domain defaults, never to a copy of them.</strong>
/// Where the wire type leaves a value null, the mapper leaves the corresponding domain property
/// unset so the record's own initializer supplies it. Writing <c>request.Temperature ?? 0.7</c> would
/// restate a default that already exists on <see cref="LlmCallConfig"/>, and the two would drift the
/// first time one of them changed.
/// </para>
/// <para>
/// <strong>Three domain capabilities are unreachable from the wire, by design.</strong>
/// <see cref="ToolUseConfig.IsolationLevelOverride"/> would let a caller weaken the sandbox its own
/// step runs in; <see cref="RetrievalStepConfiguration.CollectionName"/> would let a caller name a
/// corpus outside its own, which is a cross-tenant read primitive; and
/// <see cref="SubPlanConfig.InlinePlanDefinition"/> would make the request body recursive, turning
/// nesting depth into a parser concern that must be bounded before the object exists to inspect. Each
/// is left at its default here and has no wire field to set it.
/// </para>
/// </remarks>
internal static class WorkflowDefinitionMapper
{
    /// <summary>
    /// Maps an admitted definition to a plan graph, minting a fresh identifier for the plan and for
    /// every step.
    /// </summary>
    /// <param name="definition">
    /// The submitted definition. Expected to have passed <see cref="SubmitWorkflowCommandValidator"/>
    /// — this method reports the branch-shape failures it cannot structurally proceed past, but does
    /// not re-check the caps or referential integrity that validation already covers.
    /// </param>
    /// <returns>
    /// The mapped plan, or a failure naming the step whose conditional branching could not be
    /// resolved. Step order is preserved, so the persisted plan reads in the order it was submitted.
    /// </returns>
    internal static Result<PlanGraph> MapToPlanGraph(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var stepIds = definition.Steps.ToDictionary(
            step => step.Name, _ => PlanStepId.New(), StringComparer.Ordinal);

        var edges = definition.Edges
            .Select(edge => new PlanEdge(stepIds[edge.From], stepIds[edge.To], edge.Type, edge.Condition))
            .ToList();

        var steps = new List<PlanStep>(definition.Steps.Count);
        foreach (var step in definition.Steps)
        {
            var configuration = MapConfiguration(step, definition, stepIds);
            if (!configuration.IsSuccess)
                return Result<PlanGraph>.ValidationFailure(configuration.Errors);

            steps.Add(MapStep(step, stepIds[step.Name], configuration.Value!));
        }

        return Result<PlanGraph>.Success(new PlanGraph
        {
            Id = PlanId.New(),
            Name = definition.Name,
            Steps = steps,
            Edges = edges,
            Configuration = MapPlanConfiguration(definition.Configuration)
        });
    }

    private static PlanStep MapStep(WorkflowStep source, PlanStepId id, StepConfiguration configuration)
    {
        var step = new PlanStep
        {
            Id = id,
            Name = source.Name,
            Type = source.Type,
            Configuration = configuration,
            RetryPolicy = MapRetryPolicy(source.Retry),
            RequiredAutonomyLevel = source.RequiredAutonomyLevel
        };

        return source.Timeout is { } timeout ? step with { Timeout = timeout } : step;
    }

    private static PlanConfiguration MapPlanConfiguration(WorkflowExecutionSettings? source)
    {
        // MaxSubPlanDepth is absent from the wire and stays at the host's default: it is a runtime
        // recursion guard, and a caller able to raise it could nest arbitrarily deep whatever the
        // admission cap said.
        var configuration = new PlanConfiguration();
        if (source is null)
            return configuration;

        if (source.PlanTimeout is { } planTimeout)
            configuration = configuration with { PlanTimeout = planTimeout };

        if (source.MaxParallelSteps is { } maxParallel)
            configuration = configuration with { MaxParallelSteps = maxParallel };

        return configuration;
    }

    private static RetryPolicy MapRetryPolicy(WorkflowRetrySettings? source)
    {
        var policy = new RetryPolicy();
        if (source is null)
            return policy;

        if (source.MaxRetries is { } maxRetries)
            policy = policy with { MaxRetries = maxRetries };

        if (source.InitialDelay is { } initialDelay)
            policy = policy with { InitialDelay = initialDelay };

        if (source.Strategy is { } strategy)
            policy = policy with { Strategy = strategy };

        if (source.OnExhausted is { } onExhausted)
            policy = policy with { OnExhausted = onExhausted };

        return policy;
    }

    private static Result<StepConfiguration> MapConfiguration(
        WorkflowStep step,
        WorkflowDefinition definition,
        IReadOnlyDictionary<string, PlanStepId> stepIds) => step.Configuration switch
        {
            LlmCallStepConfiguration source => Result<StepConfiguration>.Success(MapLlmCall(source)),
            ToolUseStepConfiguration source => Result<StepConfiguration>.Success(
                new ToolUseConfig { ToolName = source.ToolName, InputParameters = source.InputParameters }),
            HumanGateStepConfiguration source => Result<StepConfiguration>.Success(MapHumanGate(source)),
            RetrievalWorkflowStepConfiguration source => Result<StepConfiguration>.Success(
                new RetrievalStepConfiguration
                {
                    Query = source.Query,
                    Strategy = source.Strategy,
                    TopK = source.TopK,
                    UseMultiSource = source.UseMultiSource
                }),
            SubPlanStepConfiguration source => Result<StepConfiguration>.Success(
                new SubPlanConfig
                {
                    ChildPlanId = new PlanId(source.ChildWorkflowId),
                    IsolateContext = source.IsolateContext
                }),
            ConditionalBranchStepConfiguration source =>
                MapConditionalBranch(source, step.Name, definition, stepIds),
            _ => Result<StepConfiguration>.ValidationFailure(
                [$"Step '{step.Name}' carries an unrecognized configuration type."])
        };

    private static LlmCallConfig MapLlmCall(LlmCallStepConfiguration source)
    {
        var configuration = new LlmCallConfig
        {
            SystemPrompt = source.SystemPrompt,
            ModelDeploymentKey = source.ModelDeploymentKey
        };

        if (source.Temperature is { } temperature)
            configuration = configuration with { Temperature = temperature };

        if (source.MaxTokens is { } maxTokens)
            configuration = configuration with { MaxTokens = maxTokens };

        return configuration;
    }

    private static HumanGateConfig MapHumanGate(HumanGateStepConfiguration source)
    {
        var configuration = new HumanGateConfig
        {
            EscalationMessage = source.EscalationMessage,
            ApprovalStrategy = source.ApprovalStrategy,
            Approvers = source.Approvers
        };

        if (source.RiskLevel is { } riskLevel)
            configuration = configuration with { RiskLevel = riskLevel };

        if (source.Timeout is { } timeout)
            configuration = configuration with { Timeout = timeout };

        return configuration;
    }

    /// <summary>
    /// Builds a branch configuration by reading the step's outgoing labelled edges, which are the
    /// submission's only statement of where each outcome goes.
    /// </summary>
    /// <remarks>
    /// The failure cases here duplicate rules <see cref="SubmitWorkflowCommandValidator"/> also
    /// enforces, and that duplication is deliberate rather than an oversight:
    /// <see cref="ConditionalBranchConfig"/> declares both targets <c>required</c>, so this method has
    /// no way to proceed when they cannot be resolved and must have an answer for the case regardless
    /// of what ran upstream. Validating there as well is what gives a caller every problem with its
    /// definition at once instead of one per round trip.
    /// </remarks>
    private static Result<StepConfiguration> MapConditionalBranch(
        ConditionalBranchStepConfiguration source,
        string stepName,
        WorkflowDefinition definition,
        IReadOnlyDictionary<string, PlanStepId> stepIds)
    {
        var outgoing = definition.Edges.Where(e => string.Equals(e.From, stepName, StringComparison.Ordinal));

        var trueTarget = SingleTargetOf(outgoing, EdgeType.ConditionalTrue);
        var falseTarget = SingleTargetOf(outgoing, EdgeType.ConditionalFalse);

        if (trueTarget is null || falseTarget is null)
        {
            return Result<StepConfiguration>.ValidationFailure(
                [$"Conditional step '{stepName}' requires exactly one outgoing ConditionalTrue edge and "
                 + "exactly one outgoing ConditionalFalse edge."]);
        }

        return Result<StepConfiguration>.Success(new ConditionalBranchConfig
        {
            ConditionExpression = source.ConditionExpression,
            TrueEdgeTargetId = stepIds[trueTarget],
            FalseEdgeTargetId = stepIds[falseTarget]
        });
    }

    /// <summary>
    /// The name of the single step reached by an edge of <paramref name="type"/>, or
    /// <see langword="null"/> when there is not exactly one. Two edges labelled the same way are two
    /// answers to one question, which is the ambiguity the edge-only contract exists to prevent.
    /// </summary>
    private static string? SingleTargetOf(IEnumerable<WorkflowEdge> outgoing, EdgeType type)
    {
        var matches = outgoing.Where(e => e.Type == type).Take(2).ToList();
        return matches.Count == 1 ? matches[0].To : null;
    }
}
