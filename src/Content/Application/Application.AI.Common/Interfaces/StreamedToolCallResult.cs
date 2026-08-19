namespace Application.AI.Common.Interfaces;

/// <summary>
/// A tool call's result as streamed to a live client via <see cref="IAgentTurnStreamSink.EmitToolCallResultAsync"/>.
/// </summary>
/// <param name="Text">
/// The tool result's redacted text. Never truncated — when <paramref name="Withheld"/> is
/// <see langword="true"/>, this is empty, not a partial or truncated version of the real result.
/// </param>
/// <param name="Withheld">
/// <see langword="true"/> when the real result was not sent — either because its length exceeded
/// <c>ToolPayloadRedactor.MaxStreamedToolCallPayloadLength</c> (results are withheld whole rather than
/// truncated, the same reasoning as <see cref="StreamedToolCallArguments.Withheld"/>: a truncated
/// preview past a redactor's structural-pass ceiling could still contain an unredacted secret), or
/// because redaction itself failed. <see langword="false"/> for a normal result, where <see cref="Text"/>
/// carries the complete, redacted output.
/// </param>
public readonly record struct StreamedToolCallResult(string Text, bool Withheld);
