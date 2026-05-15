namespace Domain.AI.Planner;

/// <summary>
/// Determines which keyed <c>IPlanStepExecutor</c> handles the step at runtime.
/// Each value maps to a specific executor registered via keyed dependency injection.
/// </summary>
public enum StepType
{
    /// <summary>Delegates to <c>RunConversationCommand</c> for LLM inference.</summary>
    LlmCall,

    /// <summary>Routes tool execution through the appropriate sandbox.</summary>
    ToolUse,

    /// <summary>Non-blocking escalation requiring human approval before proceeding.</summary>
    HumanGate,

    /// <summary>Evaluates a condition expression and activates the true or false edge.</summary>
    ConditionalBranch,

    /// <summary>Invokes a child plan in an isolated scope with depth limiting.</summary>
    SubPlanInvocation
}
