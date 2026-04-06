namespace Domain.AI.Models;

/// <summary>
/// Represents the outcome of a tool execution. Returned by <c>ITool.ExecuteAsync</c>
/// and consumed by the tool converter to build <c>FunctionResultContent</c> for the LLM.
/// </summary>
/// <remarks>
/// A tool either succeeds with output or fails with an error message.
/// The output is always a string — JSON for structured data, plain text otherwise.
/// The LLM receives the output or error as the function result in the conversation.
/// </remarks>
public record ToolResult
{
    /// <summary>Gets whether the tool execution succeeded.</summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Gets the tool output on success. JSON for structured data, plain text otherwise.
    /// Null when <see cref="Success"/> is false.
    /// </summary>
    public string? Output { get; init; }

    /// <summary>
    /// Gets the error message on failure. Null when <see cref="Success"/> is true.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>Creates a successful result with output.</summary>
    public static ToolResult Ok(string output) => new() { Success = true, Output = output };

    /// <summary>Creates a failure result with an error message.</summary>
    public static ToolResult Fail(string error) => new() { Success = false, Error = error };
}
