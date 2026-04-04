namespace Application.Common.Exceptions;

/// <summary>
/// Abstract base class for all application-specific exceptions in the harness template.
/// Provides a common type for global exception handlers to catch and process application
/// exceptions distinctly from framework or system exceptions.
/// </summary>
/// <remarks>
/// All custom exception types in the <c>ExceptionTypes</c> namespace inherit from this base.
/// This enables a single <c>catch (ApplicationExceptionBase)</c> in middleware or global
/// exception filters to handle all domain exceptions uniformly, while still allowing
/// specific handling via the concrete types.
/// <para>
/// This base class intentionally does not add properties or behavior beyond
/// <see cref="Exception"/>. Its purpose is purely hierarchical — to distinguish
/// "our exceptions" from "their exceptions" in catch blocks and logging.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Global exception handler middleware
/// catch (ApplicationExceptionBase ex) when (ex is EntityNotFoundException)
/// {
///     return Results.NotFound(ex.Message);
/// }
/// catch (ApplicationExceptionBase ex) when (ex is ValidationException validationEx)
/// {
///     return Results.BadRequest(validationEx.Errors);
/// }
/// catch (ApplicationExceptionBase ex)
/// {
///     logger.LogWarning(ex, "Unhandled application exception");
///     return Results.Problem(ex.Message);
/// }
/// </code>
/// </example>
public abstract class ApplicationExceptionBase : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ApplicationExceptionBase"/> class.
    /// </summary>
    protected ApplicationExceptionBase()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ApplicationExceptionBase"/> class
    /// with a specified error message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    protected ApplicationExceptionBase(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ApplicationExceptionBase"/> class
    /// with a specified error message and a reference to the inner exception that caused it.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that is the cause of the current exception, or <c>null</c>.</param>
    protected ApplicationExceptionBase(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
