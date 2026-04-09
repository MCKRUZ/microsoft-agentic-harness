namespace Domain.Common;

/// <summary>
/// Represents the outcome of an operation that can succeed or fail with structured errors.
/// Use as the standard return type for commands and queries to avoid exception-driven flow control.
/// </summary>
/// <remarks>
/// <para>
/// Per project conventions: "Commands return <c>Result&lt;T&gt;</c>, never throw for expected failures."
/// Validation errors, authorization denials, and business rule violations are expected outcomes —
/// not exceptional conditions — and should be communicated via <c>Result</c> rather than exceptions.
/// </para>
/// <para>
/// MediatR pipeline behaviors (validation, authorization, content safety) check if <c>TResponse</c>
/// is assignable from <c>Result</c> and return failure results instead of throwing. Handlers that
/// return non-Result types will still get exception-based error handling as a fallback.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // In a handler:
/// public Task&lt;Result&lt;Order&gt;&gt; Handle(CreateOrderCommand request, CancellationToken ct)
/// {
///     if (!await _inventory.HasStock(request.ProductId))
///         return Result&lt;Order&gt;.Fail("Product is out of stock.");
///
///     var order = new Order(...);
///     return Result&lt;Order&gt;.Success(order);
/// }
///
/// // In a caller:
/// var result = await mediator.Send(command);
/// if (!result.IsSuccess)
///     logger.LogWarning("Order failed: {Errors}", result.Errors);
/// </code>
/// </example>
// Intentionally a class, not a record: Result uses inheritance (Result<T> : Result) and
// record equality semantics with polymorphic hierarchies cause subtle bugs. Value equality
// is not meaningful for a result envelope — reference identity is sufficient.
public class Result
{
    /// <summary>Gets whether the operation succeeded.</summary>
    public bool IsSuccess { get; }

    /// <summary>Gets the error messages if the operation failed.</summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>Gets the failure category for non-success results.</summary>
    public ResultFailureType FailureType { get; }

    /// <summary>
    /// Initializes a new <see cref="Result"/>.
    /// </summary>
    protected Result(bool isSuccess, IReadOnlyList<string>? errors = null, ResultFailureType failureType = ResultFailureType.None)
    {
        IsSuccess = isSuccess;
        Errors = errors ?? [];
        FailureType = isSuccess ? ResultFailureType.None : failureType;
    }

    /// <summary>Creates a successful result with no value.</summary>
    public static Result Success() => new(true);

    /// <summary>Creates a failure result with error messages.</summary>
    public static Result Fail(params string[] errors) => new(false, errors, ResultFailureType.General);

    /// <summary>Creates a validation failure result.</summary>
    public static Result ValidationFailure(IReadOnlyList<string> errors) => new(false, errors, ResultFailureType.Validation);

    /// <summary>Creates an unauthorized failure result.</summary>
    public static Result Unauthorized(string reason) => new(false, [reason], ResultFailureType.Unauthorized);

    /// <summary>Creates a forbidden failure result.</summary>
    public static Result Forbidden(string reason) => new(false, [reason], ResultFailureType.Forbidden);

    /// <summary>Creates a content safety failure result.</summary>
    public static Result ContentBlocked(string reason) => new(false, [reason], ResultFailureType.ContentBlocked);

    /// <summary>Creates a not-found failure result.</summary>
    public static Result NotFound(string reason) => new(false, [reason], ResultFailureType.NotFound);

    /// <summary>Creates a permission-required failure result.</summary>
    public static Result PermissionRequired(string reason) => new(false, [reason], ResultFailureType.PermissionRequired);
}

/// <summary>
/// Represents the outcome of an operation that returns a value of type <typeparamref name="T"/>.
/// </summary>
/// <typeparam name="T">The type of the value returned on success.</typeparam>
public sealed class Result<T> : Result
{
    /// <summary>Gets the value if the operation succeeded. Default when failed.</summary>
    public T? Value { get; }

    private Result(bool isSuccess, T? value = default, IReadOnlyList<string>? errors = null, ResultFailureType failureType = ResultFailureType.None)
        : base(isSuccess, errors, failureType)
    {
        Value = value;
    }

    /// <summary>Creates a successful result with a value.</summary>
    public static Result<T> Success(T value) => new(true, value);

    /// <summary>Creates a failure result with error messages.</summary>
    public new static Result<T> Fail(params string[] errors) => new(false, errors: errors, failureType: ResultFailureType.General);

    /// <summary>Creates a validation failure result.</summary>
    public new static Result<T> ValidationFailure(IReadOnlyList<string> errors) => new(false, errors: errors, failureType: ResultFailureType.Validation);

    /// <summary>Creates an unauthorized failure result.</summary>
    public new static Result<T> Unauthorized(string reason) => new(false, errors: [reason], failureType: ResultFailureType.Unauthorized);

    /// <summary>Creates a forbidden failure result.</summary>
    public new static Result<T> Forbidden(string reason) => new(false, errors: [reason], failureType: ResultFailureType.Forbidden);

    /// <summary>Creates a content safety failure result.</summary>
    public new static Result<T> ContentBlocked(string reason) => new(false, errors: [reason], failureType: ResultFailureType.ContentBlocked);

    /// <summary>Creates a not-found failure result.</summary>
    public new static Result<T> NotFound(string reason) => new(false, errors: [reason], failureType: ResultFailureType.NotFound);

    /// <summary>Creates a permission-required failure result.</summary>
    public new static Result<T> PermissionRequired(string reason) => new(false, errors: [reason], failureType: ResultFailureType.PermissionRequired);

    /// <summary>
    /// Implicit conversion from a non-null value to a successful result.
    /// Throws <see cref="ArgumentNullException"/> if value is null to prevent
    /// silently creating a "successful" result with no value.
    /// </summary>
    public static implicit operator Result<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Success(value);
    }
}

/// <summary>
/// Categorizes the type of failure in a <see cref="Result"/>.
/// Enables pipeline behaviors and middleware to map failures to appropriate
/// HTTP status codes or agent recovery strategies.
/// </summary>
public enum ResultFailureType
{
    /// <summary>No failure (success).</summary>
    None = 0,
    /// <summary>General failure.</summary>
    General,
    /// <summary>Input validation failure (400).</summary>
    Validation,
    /// <summary>Authentication required (401).</summary>
    Unauthorized,
    /// <summary>Insufficient permissions (403).</summary>
    Forbidden,
    /// <summary>Content blocked by safety middleware.</summary>
    ContentBlocked,
    /// <summary>Entity not found (404).</summary>
    NotFound,
    /// <summary>Permission check requires user confirmation before proceeding.</summary>
    PermissionRequired
}
