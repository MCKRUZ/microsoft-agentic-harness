using Application.Common.Exceptions;

namespace Application.Common.Exceptions.ExceptionTypes;

/// <summary>
/// Represents an exception thrown when an operation completes successfully but returns no content.
/// This exception maps to HTTP 204 No Content status codes and signals a successful-but-empty result.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Prefer <c>Result&lt;T&gt;</c> over this exception for flow control.</strong>
/// This exception exists for compatibility with middleware that maps exception types to HTTP status
/// codes. For new code, return a <c>Result&lt;T&gt;</c> with a <c>NoContent</c> variant instead of
/// throwing this exception. Using exceptions for non-error flow control is an anti-pattern.
/// </para>
/// <para>
/// This exception is semantically distinct from error exceptions — it represents a successful
/// operation state rather than a failure. It is also distinct from <see cref="EntityNotFoundException"/>,
/// which indicates an entity was expected to exist but does not. Common scenarios include:
/// </para>
/// <list type="bullet">
///   <item><description>Successful DELETE operations with no response body</description></item>
///   <item><description>Search queries that match zero results</description></item>
///   <item><description>PUT operations that update without returning the updated resource</description></item>
///   <item><description>Cache clearing operations that complete without output</description></item>
///   <item><description>Session termination or logout operations</description></item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// var products = await repository.SearchAsync(searchTerm);
/// if (!products.Any())
/// {
///     throw new NoContentException($"No products found matching '{searchTerm}'.");
/// }
/// </code>
/// </example>
public sealed class NoContentException : ApplicationExceptionBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NoContentException"/> class
    /// with a default error message.
    /// </summary>
    public NoContentException()
        : base("The operation completed successfully but returned no content.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NoContentException"/> class
    /// with a specified message.
    /// </summary>
    /// <param name="message">A message describing why no content was returned.</param>
    public NoContentException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NoContentException"/> class
    /// with a specified message and a reference to the inner exception that caused it.
    /// </summary>
    /// <param name="message">A message describing why no content was returned.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public NoContentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
