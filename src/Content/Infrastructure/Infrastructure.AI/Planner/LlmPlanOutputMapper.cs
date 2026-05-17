using System.Text.Json;
using Domain.AI.Planner;

namespace Infrastructure.AI.Planner;

/// <summary>
/// Maps deserialized <see cref="LlmPlanOutput"/> DTOs to domain <see cref="PlanGraph"/> types.
/// Uses a two-pass approach: first assigns all step IDs, then builds step objects.
/// This ensures forward references in ConditionalBranch targets resolve correctly.
/// </summary>
internal static class LlmPlanOutputMapper
{
    public static PlanGraph MapToPlanGraph(LlmPlanOutput output)
    {
        var planId = PlanId.New();
        var nameToId = new Dictionary<string, PlanStepId>(output.Steps.Count, StringComparer.OrdinalIgnoreCase);

        // Pass 1: assign IDs so all names are resolvable (fixes forward-reference issue)
        foreach (var stepOutput in output.Steps)
            nameToId[stepOutput.Name] = PlanStepId.New();

        // Pass 2: build step objects with full name resolution
        var steps = new List<PlanStep>(output.Steps.Count);
        foreach (var stepOutput in output.Steps)
        {
            var stepId = nameToId[stepOutput.Name];
            var stepType = ParseStepType(stepOutput.Type);
            var configuration = DeserializeConfiguration(stepOutput.Configuration, stepType, nameToId);
            var retryPolicy = MapRetryPolicy(stepOutput.RetryPolicy);

            steps.Add(new PlanStep
            {
                Id = stepId,
                Name = stepOutput.Name,
                Type = stepType,
                Configuration = configuration,
                RetryPolicy = retryPolicy,
                Timeout = TimeSpan.FromSeconds(stepOutput.TimeoutSeconds)
            });
        }

        var edges = MapEdges(output.Edges, nameToId);
        var config = MapConfiguration(output.Configuration);

        return new PlanGraph
        {
            Id = planId,
            Name = output.Name,
            Steps = steps,
            Edges = edges,
            Configuration = config
        };
    }

    internal static StepType ParseStepType(string type) => type switch
    {
        "LlmCall" => StepType.LlmCall,
        "ToolUse" => StepType.ToolUse,
        "HumanGate" => StepType.HumanGate,
        "ConditionalBranch" => StepType.ConditionalBranch,
        "SubPlanInvocation" => StepType.SubPlanInvocation,
        _ => throw new InvalidOperationException($"Unknown step type: '{type}'.")
    };

    internal static EdgeType ParseEdgeType(string type) => type switch
    {
        "DataFlow" => EdgeType.DataFlow,
        "ControlFlow" => EdgeType.ControlFlow,
        "ConditionalTrue" => EdgeType.ConditionalTrue,
        "ConditionalFalse" => EdgeType.ConditionalFalse,
        _ => EdgeType.ControlFlow
    };

    private static List<PlanEdge> MapEdges(IReadOnlyList<LlmEdgeOutput> edgeOutputs, Dictionary<string, PlanStepId> nameToId)
    {
        var edges = new List<PlanEdge>(edgeOutputs.Count);
        foreach (var edgeOutput in edgeOutputs)
        {
            if (!nameToId.TryGetValue(edgeOutput.From, out var fromId))
                throw new InvalidOperationException($"Edge references unknown step '{edgeOutput.From}'.");
            if (!nameToId.TryGetValue(edgeOutput.To, out var toId))
                throw new InvalidOperationException($"Edge references unknown step '{edgeOutput.To}'.");

            edges.Add(new PlanEdge(fromId, toId, ParseEdgeType(edgeOutput.Type), edgeOutput.Condition));
        }
        return edges;
    }

    private static StepConfiguration DeserializeConfiguration(
        JsonElement configElement,
        StepType stepType,
        Dictionary<string, PlanStepId> nameToId)
    {
        return stepType switch
        {
            StepType.LlmCall => DeserializeLlmCallConfig(configElement),
            StepType.ToolUse => DeserializeToolUseConfig(configElement),
            StepType.HumanGate => DeserializeHumanGateConfig(configElement),
            StepType.ConditionalBranch => DeserializeConditionalBranchConfig(configElement, nameToId),
            StepType.SubPlanInvocation => DeserializeSubPlanConfig(configElement),
            _ => throw new InvalidOperationException($"No config deserializer for step type '{stepType}'.")
        };
    }

    private static LlmCallConfig DeserializeLlmCallConfig(JsonElement el)
    {
        return new LlmCallConfig
        {
            SystemPrompt = el.GetPropertyOrDefault("systemPrompt", ""),
            ModelDeploymentKey = el.GetPropertyOrDefault("modelDeploymentKey", "gpt-4o"),
            Temperature = el.TryGetProperty("temperature", out var temp) ? temp.GetDouble() : 0.7,
            MaxTokens = el.TryGetProperty("maxTokens", out var mt) ? mt.GetInt32() : 4096
        };
    }

    private static ToolUseConfig DeserializeToolUseConfig(JsonElement el)
    {
        var inputParams = new Dictionary<string, object?>();
        if (el.TryGetProperty("inputParameters", out var paramsEl) && paramsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in paramsEl.EnumerateObject())
                inputParams[prop.Name] = GetJsonValue(prop.Value);
        }

        return new ToolUseConfig
        {
            ToolName = el.GetPropertyOrDefault("toolName", "unknown"),
            InputParameters = inputParams
        };
    }

    private static HumanGateConfig DeserializeHumanGateConfig(JsonElement el)
    {
        var description = el.GetPropertyOrDefault("description", "Approval required");
        var timeoutMinutes = el.TryGetProperty("timeoutMinutes", out var tm) ? tm.GetInt32() : 60;

        return new HumanGateConfig
        {
            EscalationMessage = description,
            ApprovalStrategy = ApprovalStrategy.AnyOf,
            Timeout = TimeSpan.FromMinutes(timeoutMinutes)
        };
    }

    private static ConditionalBranchConfig DeserializeConditionalBranchConfig(
        JsonElement el,
        Dictionary<string, PlanStepId> nameToId)
    {
        var conditionExpr = el.GetPropertyOrDefault("conditionExpression", "true");
        var trueName = el.GetPropertyOrDefault("trueTarget", "");
        var falseName = el.GetPropertyOrDefault("falseTarget", "");

        if (!nameToId.TryGetValue(trueName, out var trueId))
            throw new InvalidOperationException($"ConditionalBranch trueTarget references unknown step '{trueName}'.");
        if (!nameToId.TryGetValue(falseName, out var falseId))
            throw new InvalidOperationException($"ConditionalBranch falseTarget references unknown step '{falseName}'.");

        return new ConditionalBranchConfig
        {
            ConditionExpression = conditionExpr,
            TrueEdgeTargetId = trueId,
            FalseEdgeTargetId = falseId
        };
    }

    private static SubPlanConfig DeserializeSubPlanConfig(JsonElement el)
    {
        var isolate = !el.TryGetProperty("isolateContext", out var ic) || ic.GetBoolean();
        return new SubPlanConfig { IsolateContext = isolate };
    }

    internal static RetryPolicy MapRetryPolicy(LlmRetryPolicyOutput? output)
    {
        if (output is null)
            return new RetryPolicy();

        return new RetryPolicy
        {
            MaxRetries = output.MaxRetries,
            InitialDelay = TimeSpan.FromMilliseconds(output.InitialDelayMs),
            Strategy = ParseBackoffStrategy(output.Strategy),
            OnExhausted = ParseErrorRecovery(output.OnExhausted)
        };
    }

    private static BackoffStrategy ParseBackoffStrategy(string strategy) => strategy switch
    {
        "Exponential" => BackoffStrategy.Exponential,
        "Linear" => BackoffStrategy.Linear,
        "Fixed" => BackoffStrategy.Fixed,
        _ => BackoffStrategy.Exponential
    };

    private static ErrorRecovery ParseErrorRecovery(string recovery) => recovery switch
    {
        "FailStep" => ErrorRecovery.FailStep,
        "SkipStep" => ErrorRecovery.SkipStep,
        "FailPlan" => ErrorRecovery.FailPlan,
        _ => ErrorRecovery.FailStep
    };

    private static PlanConfiguration MapConfiguration(LlmPlanConfigOutput? output)
    {
        if (output is null)
            return new PlanConfiguration();

        return new PlanConfiguration
        {
            PlanTimeout = TimeSpan.FromMinutes(output.PlanTimeoutMinutes),
            MaxParallelSteps = output.MaxParallelSteps,
            MaxSubPlanDepth = output.MaxSubPlanDepth
        };
    }

    private static object? GetJsonValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => el.GetRawText()
    };
}
