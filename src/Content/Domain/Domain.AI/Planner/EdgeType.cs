namespace Domain.AI.Planner;

/// <summary>
/// Classifies the relationship between two connected <see cref="PlanStep"/> nodes
/// in a <see cref="PlanGraph"/>.
/// </summary>
public enum EdgeType
{
    /// <summary>Output of the source step feeds as input to the target step.</summary>
    DataFlow,

    /// <summary>Source step must complete before the target step starts.</summary>
    ControlFlow,

    /// <summary>Edge followed when a conditional branch evaluates to true.</summary>
    ConditionalTrue,

    /// <summary>Edge followed when a conditional branch evaluates to false.</summary>
    ConditionalFalse
}
