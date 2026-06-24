using Application.AI.Common.Interfaces.Governance;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// Ambient accessor that bridges the per-turn scoped <see cref="IProgressEvaluator"/> to the agent's
/// converted tool functions.
/// </summary>
/// <remarks>
/// Mirrors <see cref="ToolGovernanceAccessor"/>: agents (and their captured tool-invocation lambdas)
/// are cached across turns, so the governed tool wrapper cannot capture a scoped evaluator at build
/// time — it would go stale. The turn handler sets <see cref="Current"/> to the live scoped evaluator
/// at the start of each turn and clears it in a <c>finally</c>; the wrapper reads it at invocation
/// time. When unset (a tool invoked outside a governed turn), the wrapper skips progress evaluation.
/// </remarks>
public static class ProgressGuardAccessor
{
    private static readonly AsyncLocal<IProgressEvaluator?> s_current = new();

    /// <summary>The progress evaluator for the current async flow, or null when not inside a governed turn.</summary>
    public static IProgressEvaluator? Current
    {
        get => s_current.Value;
        set => s_current.Value = value;
    }
}
