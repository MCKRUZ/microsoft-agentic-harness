namespace Application.AI.Common.Interfaces;

/// <summary>
/// A tool call's arguments as streamed to a live client via <see cref="IAgentTurnStreamSink.EmitToolCallAsync"/>.
/// </summary>
/// <param name="Json">
/// The tool call's arguments, serialized as JSON. Always complete, parseable JSON — never
/// truncated, and never an incremental delta. When <paramref name="Withheld"/> is <see langword="true"/>,
/// this is the fixed placeholder <c>"{}"</c>, not a partial or truncated version of the real
/// arguments.
/// </param>
/// <param name="Withheld">
/// <see langword="true"/> when the real arguments were not sent — either because their serialized
/// length exceeded <c>ToolPayloadRedactor.MaxStreamedToolCallArgsLength</c> (arguments are withheld
/// whole rather than truncated, since truncating mid-JSON would hand the client invalid data), or
/// because serialization/redaction itself failed. <see langword="false"/> for a normal call, where
/// <see cref="Json"/> carries the complete, redacted arguments.
/// </param>
public readonly record struct StreamedToolCallArguments(string Json, bool Withheld);
