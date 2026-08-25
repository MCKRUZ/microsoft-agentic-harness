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
