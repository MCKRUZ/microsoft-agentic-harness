using Application.Common.Exceptions;

namespace Application.Common.Exceptions.ExceptionTypes;

/// <summary>
/// Represents an exception thrown when a database operation fails to complete successfully.
/// This exception provides structured context about the operation type, entity, and optional
/// identifier involved in the failure.
/// </summary>
/// <remarks>
/// Use this exception in repository or data access layers to wrap database failures with
/// domain-meaningful context. It can be caught by upper layers to return HTTP 500 or 400
/// responses depending on the nature of the failure. Common scenarios include:
/// <list type="bullet">
///   <item><description>Database connection failures</description></item>
///   <item><description>Constraint violations (unique, foreign key, check)</description></item>
///   <item><description>Transaction rollbacks</description></item>
///   <item><description>Timeout errors during database operations</description></item>
///   <item><description>Data integrity issues</description></item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// catch (DbUpdateException ex)
/// {
///     throw new DatabaseInteractionException("Create", "User", null, ex);
/// }
/// </code>
/// </example>
public sealed class DatabaseInteractionException : ApplicationExceptionBase
{
    /// <summary>
    /// Gets the type of database operation that failed, if specified.
    /// </summary>
    /// <value>The operation name (e.g., "Create", "Update", "Delete", "Read"), or <c>null</c> if not provided.</value>
    public string? Operation { get; }

    /// <summary>
    /// Gets the name or type of the entity involved in the failed operation, if specified.
    /// </summary>
    /// <value>The entity type name (e.g., "User", "Order"), or <c>null</c> if not provided.</value>
    public string? EntityName { get; }

    /// <summary>
    /// Gets the identifier of the entity involved in the failed operation, if specified.
    /// </summary>
    /// <value>The lookup key (supports int, Guid, string, composite keys), or <c>null</c> if not provided.</value>
    public object? Key { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseInteractionException"/> class
    /// with a default error message.
    /// </summary>
    public DatabaseInteractionException()
        : base("A database operation failed to complete successfully.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseInteractionException"/> class
    /// with a custom error message.
    /// </summary>
    /// <param name="message">A message describing the database failure.</param>
    public DatabaseInteractionException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseInteractionException"/> class
    /// with a custom error message and a reference to the inner exception that caused it.
    /// </summary>
    /// <param name="message">A message describing the database failure.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public DatabaseInteractionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseInteractionException"/> class
    /// with structured context about the failed operation.
    /// </summary>
    /// <param name="operation">The database operation that failed (e.g., "Create", "Update", "Delete").</param>
    /// <param name="entityName">The entity type involved (e.g., "User", "Product").</param>
    /// <param name="key">
    /// The optional identifier of the entity. Supports any key type (int, Guid, string, etc.).
    /// Pass <c>null</c> for operations that don't target a specific entity instance.
    /// </param>
    /// <param name="innerException">The optional underlying exception that caused the failure.</param>
    /// <example>
    /// <code>
    /// throw new DatabaseInteractionException("Update", "Product", productId);
    /// // Message: "There was an issue performing a 'Update' for entity 'Product' with key '456'."
    ///
    /// throw new DatabaseInteractionException("Create", "Order", null);
    /// // Message: "There was an issue performing a 'Create' for entity 'Order'."
    /// </code>
    /// </example>
    public DatabaseInteractionException(
        string operation,
        string entityName,
        object? key = null,
        Exception? innerException = null)
        : base(
            $"There was an issue performing a '{operation}' for entity '{entityName}'"
            + (key is not null ? $" with key '{key}'." : "."),
            innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        Operation = operation;
        EntityName = entityName;
        Key = key;
    }
}
