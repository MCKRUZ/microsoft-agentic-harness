using Application.Common.Exceptions;

namespace Application.Common.Exceptions.ExceptionTypes;

/// <summary>
/// Represents an exception thrown when a request is malformed, invalid, or cannot be processed
/// due to client-side errors. This exception maps directly to HTTP 400 Bad Request status codes.
/// </summary>
/// <remarks>
/// Use this exception for client-side errors rather than server-side failures. Common scenarios include:
/// <list type="bullet">
///   <item><description>Invalid request parameters or query strings</description></item>
///   <item><description>Mandatory fields missing from the request body</description></item>
///   <item><description>Data format or type validation failures</description></item>
///   <item><description>Business rule violations in request data</description></item>
///   <item><description>Invalid operation sequences (e.g., updating a non-existent resource)</description></item>
/// </list>
/// This exception should be caught by API controllers or middleware to return appropriate
/// HTTP 400 responses with the exception message as the error detail.
/// </remarks>
/// <example>
/// <code>
/// if (request.Items is null || !request.Items.Any())
/// {
///     throw new BadRequestException("Order must contain at least one item.");
/// }
/// </code>
/// </example>
public sealed class BadRequestException : ApplicationExceptionBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BadRequestException"/> class
    /// with a default error message.
    /// </summary>
    public BadRequestException()
        : base("The request was invalid or could not be processed.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BadRequestException"/> class
    /// with a specified error message.
    /// </summary>
    /// <param name="message">
    /// A user-friendly message describing why the request was invalid.
    /// Should be specific enough for the client to correct their request.
    /// </param>
    /// <example>
    /// <code>
    /// throw new BadRequestException("Start date must be before end date.");
    /// </code>
    /// </example>
    public BadRequestException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BadRequestException"/> class
    /// with a specified error message and a reference to the inner exception that caused it.
    /// </summary>
    /// <param name="message">A message describing why the request was invalid.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    /// <example>
    /// <code>
    /// catch (FormatException ex)
    /// {
    ///     throw new BadRequestException("Invalid date format in request.", ex);
    /// }
    /// </code>
    /// </example>
    public BadRequestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
