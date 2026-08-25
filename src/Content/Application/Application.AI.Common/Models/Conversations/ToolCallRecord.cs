namespace Application.AI.Common.Models.Conversations;

/// <summary>Captures a single tool invocation within an assistant turn.</summary>
/// <param name="ToolName">The invoked tool's name.</param>
/// <param name="Input">
/// The tool's arguments, treated for durable/model-facing replay (see
/// <c>IToolCallReplayTreatment</c>) — JSON-serialized when the treated text is still valid JSON, but
/// not guaranteed to be: a truncated-with-marker or withheld-placeholder value is not parseable JSON.
/// A plain <see langword="string"/>, not <see cref="System.Text.Json.JsonElement"/>, precisely
/// because it cannot promise that guarantee. <see langword="null"/> when the call had no arguments.
/// </param>
/// <param name="Output">
/// The tool's result, treated the same way as <paramref name="Input"/> and carrying the identical
/// not-guaranteed-to-be-JSON caveat. <see langword="null"/> when the call has no recorded result —
/// see <c>ToolExchange.HasResult</c>; a caller persisting an orphaned call must still emit a
/// placeholder here, never leave the row silently missing a result.
/// </param>
/// <param name="DurationMs">Wall-clock time the invocation took.</param>
/// <param name="CallId">
/// The provider-assigned id pairing this record to its <c>FunctionCallContent</c>/
/// <c>FunctionResultContent</c> pair on replay. <see langword="null"/> for records persisted before
/// this field existed — both trailing parameters default to <see langword="null"/> specifically so
/// <see cref="System.Text.Json.JsonSerializer"/> can still deserialize an older, shorter JSON blob
/// against this record's constructor without a migration.
/// </param>
/// <param name="RoundOrdinal">
/// This call's position among every tool call the same assistant turn made, zero-based in call
/// order. A turn can invoke several tools before replying (call A, read A's result, call B) — a
/// flat, unordered list of records would replay as though every call in the turn happened in
/// parallel, discarding the "called B because A returned X" reasoning chain the calls actually
/// followed. <see langword="null"/> for records persisted before this field existed.
/// </param>
public sealed record ToolCallRecord(
    string ToolName,
    string? Input,
    string? Output,
    long DurationMs,
    string? CallId = null,
    int? RoundOrdinal = null);
