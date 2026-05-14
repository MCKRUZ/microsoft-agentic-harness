namespace Application.AI.Common.Interfaces.MediatR;

/// <summary>
/// Marker interface for MediatR responses that carry tool output eligible for
/// response sanitization. Implementations must return the same concrete type
/// from <see cref="WithSanitizedOutput"/> so the MediatR pipeline can cast back to TResponse.
/// </summary>
public interface IToolResponse
{
    /// <summary>Gets the raw tool output string to be sanitized.</summary>
    string ToolOutput { get; }

    /// <summary>
    /// Creates a new response instance with the sanitized output replacing the original.
    /// Must return the same concrete type as the implementing class.
    /// </summary>
    IToolResponse WithSanitizedOutput(string sanitizedOutput);
}
