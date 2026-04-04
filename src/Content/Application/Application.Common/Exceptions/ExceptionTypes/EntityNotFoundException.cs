using Application.Common.Exceptions;

namespace Application.Common.Exceptions.ExceptionTypes;

/// <summary>
/// Represents an exception thrown when a requested entity cannot be found in the data store.
/// This exception maps to HTTP 404 Not Found status codes and provides structured context
/// about which entity and identifier were involved.
/// </summary>
/// <remarks>
/// Use this exception in repository, service, or CQRS query handler layers when a lookup
/// operation fails to locate an entity by its identifier. Common scenarios include:
/// <list type="bullet">
///   <item><description>Database entity lookups by primary key</description></item>
///   <item><description>Repository pattern <c>GetById</c> operations</description></item>
///   <item><description>CQRS query handlers expecting a specific record</description></item>
///   <item><description>Service layer entity resolution by unique criteria</description></item>
/// </list>
/// This exception is semantically distinct from <see cref="NoContentException"/>, which indicates
/// a successful operation with no results. <c>EntityNotFoundException</c> indicates the entity
/// was expected to exist but does not.
/// </remarks>
/// <example>
/// <code>
/// var user = await dbContext.Users.FindAsync(userId)
///     ?? throw new EntityNotFoundException("User", userId);
/// </code>
/// </example>
public sealed class EntityNotFoundException : ApplicationExceptionBase
{
    /// <summary>
    /// Gets the name or type of the entity that was not found, if specified.
    /// </summary>
    /// <value>The entity type name (e.g., "User", "Product"), or <c>null</c> if not provided.</value>
    public string? EntityName { get; }

    /// <summary>
    /// Gets the identifier used to search for the entity, if specified.
    /// </summary>
    /// <value>The lookup key (supports int, Guid, string, composite keys), or <c>null</c> if not provided.</value>
    public object? Key { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="EntityNotFoundException"/> class
    /// with a default error message.
    /// </summary>
    public EntityNotFoundException()
        : base("The requested entity was not found.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="EntityNotFoundException"/> class
    /// with a custom error message.
    /// </summary>
    /// <param name="message">A message describing why the entity was not found.</param>
    /// <example>
    /// <code>
    /// throw new EntityNotFoundException("No active user found with the provided email address.");
    /// </code>
    /// </example>
    public EntityNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="EntityNotFoundException"/> class
    /// with a custom error message and a reference to the inner exception that caused it.
    /// </summary>
    /// <param name="message">A message describing why the entity was not found.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public EntityNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="EntityNotFoundException"/> class
    /// with a formatted message including the entity name and identifier.
    /// </summary>
    /// <param name="entityName">The name or type of the entity that was not found (e.g., "User", "Product").</param>
    /// <param name="key">The identifier used to search for the entity. Supports any key type (int, Guid, string, etc.).</param>
    /// <example>
    /// <code>
    /// throw new EntityNotFoundException("User", 123);
    /// // Message: "Entity \"User\" (123) was not found."
    ///
    /// throw new EntityNotFoundException("Product", "ABC-123");
    /// // Message: "Entity \"Product\" (ABC-123) was not found."
    /// </code>
    /// </example>
    public EntityNotFoundException(string entityName, object key)
        : base($"Entity \"{entityName}\" ({key}) was not found.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        ArgumentNullException.ThrowIfNull(key);
        EntityName = entityName;
        Key = key;
    }
}
