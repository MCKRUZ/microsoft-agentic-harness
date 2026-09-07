namespace Application.AI.Common.Services;

/// <summary>
/// Ambient (<see cref="AsyncLocal{T}"/>) holder for the calling application's per-turn context —
/// text a caller wants folded into this one turn only (e.g. mood, recently retrieved memory,
/// situational continuity), distinct from the conversation's persistent
/// <c>ConversationSettings.SystemPromptOverride</c>.
/// </summary>
/// <remarks>
/// <para>
/// Exists because the harness caches an agent per conversation (see <c>IAgentConversationCache</c>)
/// and builds its static instructions — <c>SystemPromptOverride</c> included — once, not per turn.
/// A caller with content that changes every message (an avatar's present-moment state, for example)
/// has nowhere to put it in that static path without breaking Anthropic prompt caching, which
/// requires the cached prefix to stay byte-identical across turns.
/// <see cref="Application.AI.Common.Services.Agent.CallerTurnContextProvider"/>
/// reads this ambient value on the framework's separate, genuinely-per-invocation
/// <c>AIContextProvider</c> rail instead, so the static instructions never change turn to turn while
/// this content still reaches the model fresh every time.
/// </para>
/// <para>
/// Seeded by <c>ExecuteAgentTurnCommandHandler</c> from <c>ExecuteAgentTurnCommand.TurnContext</c>
/// immediately before dispatch, mirroring the identical ambient-bridge pattern
/// <see cref="LlmUsageCapture.Current"/> and <see cref="ReplayedToolCallScope.Current"/> already use
/// for the same reason — the agent outlives the handler's own scope, so per-turn state has to travel
/// ambiently rather than by parameter. Cleared in the same <c>finally</c> block those two are, so a
/// turn that set no context never leaks a stale value to a later, unrelated turn sharing the same
/// async-local flow.
/// </para>
/// </remarks>
public static class CallerTurnContextScope
{
    private static readonly AsyncLocal<string?> s_current = new();

    /// <summary>
    /// The current turn's caller-supplied context, or <see langword="null"/> when the caller sent
    /// none (the common case for every agent that isn't using this feature) or no turn has set one
    /// (e.g. a test constructing the provider directly).
    /// </summary>
    public static string? Current
    {
        get => s_current.Value;
        set => s_current.Value = value;
    }
}
