namespace Domain.AI.Planner;

/// <summary>
/// Derives the two distinct identities a plan run needs, so they cannot be conflated: a
/// <em>per-step conversation id</em> and a <em>run-level budget key</em>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why these must be different.</strong> A conversation id is heavily overloaded downstream —
/// it is the sole key of <c>IAgentConversationCache</c> (which returns a cached agent on a hit and
/// ignores the requested skills and options entirely), the key of <c>ISkillCompletionTracker</c>, and
/// the observability session key. Sharing one id across concurrent plan steps therefore makes a step
/// silently run under a different step's agent — different skills, instructions, allowed tools, and
/// deployment — and lets whichever step finishes first evict the cache entry and clear the skill
/// tracking of steps still in flight. Every step consequently gets its own conversation id.
/// </para>
/// <para>
/// <strong>Why the budget key is separate.</strong> Steps each get their own conversation id, so spend
/// recorded under those ids is spread across one entry per step and never sums to what the run cost.
/// The run-level key is namespaced out of the conversation-id space so that nothing else can claim it,
/// and the plan run accumulates against it explicitly and releases it when the run finishes.
/// </para>
/// <para>
/// The two also differ in who may release them. A plan run and a plan step each own their key outright
/// — created for one execution, never resumed — and so release it on completion. An ordinary
/// conversation is the opposite: it continues across runs and across hosts (issue #235), which is why
/// <c>RunConversationCommandHandler</c> no longer releases anything. It used to, in a <c>finally</c>,
/// and that is why a budget accumulated under a conversation id it was given used to be erased the
/// moment the run ended.
/// </para>
/// </remarks>
public static class PlanRunKeys
{
    /// <summary>
    /// Prefix marking a budget entry as owned by a plan run rather than by a conversation. Also
    /// guarantees the key cannot collide with a real conversation id.
    /// </summary>
    public const string RunBudgetPrefix = "planrun:";

    /// <summary>
    /// The conversation id for a single plan step. Unique per step so agent resolution, cache
    /// eviction, skill-completion tracking, and the observability session all stay isolated.
    /// </summary>
    /// <param name="runScope">Identity of the enclosing run (its conversation id or plan id).</param>
    /// <param name="stepId">The step being executed.</param>
    /// <returns>The step's conversation id.</returns>
    public static string StepConversationId(string runScope, PlanStepId stepId) =>
        $"{runScope}:{stepId.Value}";

    /// <summary>
    /// The budget key accumulating token spend across every step of one plan run. Owned by the plan
    /// run, which releases it when the run ends — nothing else may.
    /// </summary>
    /// <param name="runScope">Identity of the run (its conversation id or plan id).</param>
    /// <returns>The run's budget key.</returns>
    public static string RunBudgetKey(string runScope) => $"{RunBudgetPrefix}{runScope}";
}
