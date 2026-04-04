using System.Collections.ObjectModel;
using Application.Common.Exceptions;
using FluentValidation.Results;

namespace Application.Common.Exceptions.ExceptionTypes;

/// <summary>
/// Represents validation errors that occur during request processing in the application.
/// This exception aggregates multiple validation failures into a single exception instance,
/// grouping them by property name for easy consumption by API responses and UI error displays.
/// </summary>
/// <remarks>
/// Typically thrown by the <c>RequestValidationBehavior</c> MediatR pipeline behavior when
/// FluentValidation detects one or more validation failures. Each property name maps to an
/// array of error messages, making it suitable for structured error responses.
/// </remarks>
/// <example>
/// <code>
/// try
/// {
///     await mediator.Send(request);
/// }
/// catch (ValidationException ex)
/// {
///     foreach (var (property, errors) in ex.Errors)
///     {
///         Console.WriteLine($"{property}: {string.Join(", ", errors)}");
///     }
/// }
/// </code>
/// </example>
public sealed class ValidationException : ApplicationExceptionBase
{
    /// <summary>
    /// Gets the dictionary of validation errors, where the key is the property name
    /// and the value is an array of error messages for that property.
    /// </summary>
    /// <value>
    /// A dictionary containing all validation failures grouped by property name.
    /// Empty when no specific property-level failures are provided.
    /// </value>
    public IReadOnlyDictionary<string, string[]> Errors { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ValidationException"/> class
    /// with a default error message and an empty errors collection.
    /// </summary>
    public ValidationException()
        : base("One or more validation failures have occurred.")
    {
        Errors = ReadOnlyDictionary<string, string[]>.Empty;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ValidationException"/> class
    /// with a specified error message.
    /// </summary>
    /// <param name="message">A message describing the validation failure.</param>
    public ValidationException(string message)
        : base(message)
    {
        Errors = ReadOnlyDictionary<string, string[]>.Empty;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ValidationException"/> class
    /// with a specified error message and a reference to the inner exception that caused it.
    /// </summary>
    /// <param name="message">A message describing the validation failure.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public ValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
        Errors = ReadOnlyDictionary<string, string[]>.Empty;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ValidationException"/> class
    /// with validation failures from FluentValidation results.
    /// </summary>
    /// <param name="failures">
    /// A collection of <see cref="ValidationFailure"/> objects representing individual validation errors.
    /// Failures are grouped by property name and converted to the <see cref="Errors"/> dictionary.
    /// </param>
    /// <remarks>
    /// Processing behavior:
    /// <list type="bullet">
    ///   <item><description>Groups failures by <see cref="ValidationFailure.PropertyName"/></description></item>
    ///   <item><description>Converts each group to an array of error messages</description></item>
    ///   <item><description>Null or empty collections result in an empty <see cref="Errors"/> dictionary</description></item>
    /// </list>
    /// </remarks>
    /// <example>
    /// <code>
    /// var failures = new List&lt;ValidationFailure&gt;
    /// {
    ///     new("Email", "Email is required"),
    ///     new("Email", "Email format is invalid"),
    ///     new("Name", "Name is required")
    /// };
    ///
    /// var exception = new ValidationException(failures);
    /// // exception.Errors["Email"] = ["Email is required", "Email format is invalid"]
    /// // exception.Errors["Name"]  = ["Name is required"]
    /// </code>
    /// </example>
    public ValidationException(IEnumerable<ValidationFailure> failures)
        : this()
    {
        Errors = (failures ?? Enumerable.Empty<ValidationFailure>())
            .GroupBy(e => e.PropertyName, e => e.ErrorMessage)
            .ToDictionary(group => group.Key, group => group.ToArray())
            .AsReadOnly();
    }
}
