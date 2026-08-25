namespace Application.AI.Common.Interfaces;

/// <summary>
/// Prepares a tool call's raw arguments or result text for durable, model-facing replay — a
/// conversation store persists the output, and a resumed conversation replays it straight back into
/// the model's context.
/// </summary>
/// <remarks>
/// A different trust boundary than <c>ToolPayloadRedactor</c>'s streaming/observability paths: this
/// content is both durable (persisted indefinitely, unlike a transient SSE frame) and genuinely
/// model-facing (the model reads it as its own memory, unlike an observability preview a human
/// reads). It therefore needs the full sanitize-then-redact treatment
/// (<c>SanitizeThenRedact.Apply</c>), not just secret redaction, and it needs the invariant that a
/// treated value is never empty when the caller needs one — see <see cref="NoResultPlaceholder"/>.
/// </remarks>
public interface IToolCallReplayTreatment
{
    /// <summary>
    /// Whether tool-call replay memory is turned on for this deployment
    /// (<c>AppConfig:AI:Conversations:ToolCallReplay:Enabled</c>). A caller extracting and persisting a
    /// turn's tool calls must check this <em>before</em> doing that work — not skip persistence after
    /// treating anyway — so a deployment that opts out for cost or compliance reasons never has tool
    /// payloads written to the conversation store in the first place.
    /// </summary>
    bool Enabled { get; }

    /// <summary>
    /// The most tool calls one turn may persist for replay
    /// (<c>AppConfig:AI:Conversations:ToolCallReplay:MaxCallsPerTurn</c>). A caller building a turn's
    /// records keeps the earliest this many by round ordinal and drops the rest, logging how many.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bounds storage growth driven by model output. Nothing else does: the framework's per-request
    /// iteration limit caps tool-calling rounds, not the calls issued in parallel inside one round.
    /// </para>
    /// <para>
    /// <strong>Stub this explicitly when mocking this interface.</strong> Zero is a meaningful value
    /// here — it drops every tool call, it does not mean "unlimited" — and a mocking framework leaves
    /// an unconfigured <see langword="int"/> member at zero. A test that stubs only
    /// <see cref="Enabled"/> therefore turns the whole feature off while still appearing to exercise
    /// it, which is exactly how this member's introduction broke an end-to-end replay test that had
    /// been passing.
    /// </para>
    /// </remarks>
    int MaxCallsPerTurn { get; }

    /// <summary>
    /// The most treated tool-call text, in characters, one replayed window may send back to the model
    /// across every row in it (<c>AppConfig:AI:Conversations:ToolCallReplay:MaxReplayedChars</c>).
    /// </summary>
    /// <remarks>
    /// The read-side counterpart to <see cref="MaxCallsPerTurn"/>, and the only one of the two that
    /// bounds per-turn prompt cost. It applies to rows already in the store, so it also bounds
    /// conversations persisted before any write-side cap existed — which a write-side cap alone, by
    /// construction, never can. The same
    /// <strong>stub this explicitly when mocking</strong> warning on <see cref="MaxCallsPerTurn"/>
    /// applies here, and bites harder: a zero read budget silently empties every replayed window.
    /// </remarks>
    int MaxReplayedChars { get; }

    /// <summary>
    /// Treats raw text — a tool call's arguments or a tool result — for safe, bounded, model-facing
    /// replay: sanitize, then redact, then size-tier the result (verbatim under the configured
    /// ceiling, truncated with a visible marker above it, withheld outright above the hard
    /// structural-redaction ceiling). Never throws — a treatment failure degrades to a placeholder
    /// rather than aborting whatever is persisting a turn.
    /// </summary>
    /// <param name="rawText">The untreated text. Empty/whitespace input passes through unchanged.</param>
    /// <param name="toolName">The tool that produced <paramref name="rawText"/>, for sanitizer context.</param>
    string Treat(string rawText, string? toolName);

    /// <summary>
    /// The honest placeholder for a tool call with no persisted result — an orphaned call (unknown-call
    /// termination, iteration-limit exhaustion, a blocked client-side tool) must still expand to a
    /// result message on replay: a persisted assistant <c>tool_calls</c> message with no matching
    /// result message is a malformed conversation a provider rejects with a 400, and because it would
    /// be <em>persisted</em>, that failure would recur on every subsequent turn with no recovery short
    /// of deleting the conversation.
    /// </summary>
    string NoResultPlaceholder { get; }
}
