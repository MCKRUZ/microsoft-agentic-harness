namespace Application.AI.Common.Services;

/// <summary>
/// Ambient (<see cref="AsyncLocal{T}"/>) holder for the current turn's
/// <see cref="ReplayedToolCallSet"/> — the mutable set of tool-call ids already known to
/// <c>ToolDiagnosticsMiddleware.AppendFunctionResultTracesAsync</c>, seeded with ids replayed from
/// earlier conversation history, then grown as the middleware itself records each genuinely new
/// result.
/// </summary>
/// <remarks>
/// <para>
/// Exists to let that middleware tell "a tool result genuinely produced this turn, not yet recorded"
/// apart from "a tool result already known" — either because it sat in replayed history before this
/// turn began, or because this same turn's own function-invocation loop already recorded it on an
/// earlier round. Both shapes arrive at that middleware identically — a
/// <see cref="Microsoft.Extensions.AI.FunctionResultContent"/> inside the inbound message list — so
/// the distinction has to come from somewhere else: this scope is that somewhere.
/// </para>
/// <para>
/// <strong>Mutability is load-bearing, not incidental.</strong> This middleware sits inside
/// <see cref="Microsoft.Extensions.AI.FunctionInvokingChatClient"/>, which calls it once per model
/// round-trip within a turn, each time with the full, growing inbound message list — round 2's list
/// still contains round 1's result, round 3's still contains rounds 1 and 2's, and so on. A read-only
/// snapshot taken once before dispatch would only ever exclude ids that existed <em>before</em> this
/// turn started, so a turn making two or more sequential tool calls would re-record round 1's result
/// again on every later round of the same turn — a duplicate this scope being mutable is what
/// prevents: the middleware adds each id to this same set the moment it records that id, so the next
/// round's scan of the (still-growing) message list correctly skips it.
/// </para>
/// <para>
/// Seeded exactly once per turn, by <c>ExecuteAgentTurnCommandHandler</c>, immediately before
/// dispatch — mirroring the identical ambient-bridge pattern <c>LlmUsageCapture.Current</c> already
/// uses for the same reason (the agent and its cached tool functions outlive the handler's own scope,
/// so state this turn needs has to travel ambiently rather than by parameter). Cleared in the same
/// <c>finally</c> block, for the same reason: leaving a stale value armed would apply one turn's seed
/// set to a later, unrelated turn sharing the same async-local flow.
/// </para>
/// <para>
/// The value is a <see cref="ReplayedToolCallSet"/> rather than a bare <see cref="HashSet{T}"/>
/// because an ambient value is not guaranteed to be reached by one thread at a time: a tool that
/// drives an <see cref="Microsoft.Extensions.AI.IChatClient"/> through the same middleware without
/// going through the turn handler inherits its parent flow's instance by reference instead of
/// rebinding its own. See that type for the concurrency contract and why the operation it exposes is
/// a single claim rather than a separate test and add.
/// </para>
/// </remarks>
public static class ReplayedToolCallScope
{
    private static readonly AsyncLocal<ReplayedToolCallSet?> s_current = new();

    /// <summary>
    /// The current turn's known-call-id set — seeded with replayed history, then grown in place as
    /// the middleware records new results — or <see langword="null"/> when no turn has set one (e.g. a
    /// test constructing the middleware directly). Callers must treat a <see langword="null"/> scope
    /// as "nothing is known yet," not as an error.
    /// </summary>
    public static ReplayedToolCallSet? Current
    {
        get => s_current.Value;
        set => s_current.Value = value;
    }
}
