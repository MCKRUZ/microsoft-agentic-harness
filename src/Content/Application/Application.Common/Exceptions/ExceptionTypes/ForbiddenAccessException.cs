using Application.Common.Exceptions;

namespace Application.Common.Exceptions.ExceptionTypes;

/// <summary>
/// Represents an exception thrown when an authenticated user attempts to perform an operation
/// without sufficient permissions. This exception maps to HTTP 403 Forbidden status codes.
/// </summary>
/// <remarks>
/// This exception is semantically distinct from authentication errors (HTTP 401 Unauthorized).
/// It indicates the server successfully identified the user, but they lack the necessary
/// privileges for the requested action. Common scenarios include:
/// <list type="bullet">
///   <item><description>Accessing resources requiring elevated permissions</description></item>
///   <item><description>Modifying data the user is not authorized to change</description></item>
///   <item><description>Accessing functionality reserved for specific roles</description></item>
///   <item><description>Operating on resources belonging to other users</description></item>
///   <item><description>Exceeding rate limits or quota restrictions</description></item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// if (document.OwnerId != currentUser.Id &amp;&amp; !currentUser.IsAdmin)
/// {
///     throw new ForbiddenAccessException("You do not have permission to access this document.");
/// }
/// </code>
/// </example>
public sealed class ForbiddenAccessException : ApplicationExceptionBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ForbiddenAccessException"/> class
    /// with a default error message.
    /// </summary>
    public ForbiddenAccessException()
        : base("User is not authorized to perform this action.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ForbiddenAccessException"/> class
    /// with a specified error message.
    /// </summary>
    /// <param name="message">
    /// A message describing why access was forbidden. Should explain what permission
    /// is missing and, where possible, how to obtain it.
    /// </param>
    /// <example>
    /// <code>
    /// throw new ForbiddenAccessException("Only administrators can delete user accounts.");
    /// </code>
    /// </example>
    public ForbiddenAccessException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ForbiddenAccessException"/> class
    /// with a specified error message and a reference to the inner exception that caused it.
    /// </summary>
    /// <param name="message">A message describing why access was forbidden.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public ForbiddenAccessException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
