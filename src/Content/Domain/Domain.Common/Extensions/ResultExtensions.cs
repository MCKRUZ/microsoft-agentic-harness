namespace Domain.Common.Extensions;

/// <summary>
/// Fluent composition extensions for <see cref="Result{T}"/> enabling functional-style
/// chaining. All methods preserve the original <see cref="ResultFailureType"/> when propagating failures.
/// </summary>
public static class ResultExtensions
{
    /// <summary>
    /// Transforms the value of a successful result. Propagates failure unchanged.
    /// </summary>
    public static Result<TOut> Map<T, TOut>(this Result<T> result, Func<T, TOut> mapper)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        return result.IsSuccess
            ? Result<TOut>.Success(mapper(result.Value!))
            : PropagateFailure<T, TOut>(result);
    }

    /// <summary>
    /// Chains a result-producing operation onto a successful result. Propagates failure unchanged.
    /// </summary>
    public static Result<TOut> Bind<T, TOut>(this Result<T> result, Func<T, Result<TOut>> binder)
    {
        ArgumentNullException.ThrowIfNull(binder);
        return result.IsSuccess
            ? binder(result.Value!)
            : PropagateFailure<T, TOut>(result);
    }

    /// <summary>
    /// Validates the value of a successful result against a predicate.
    /// </summary>
    public static Result<T> Ensure<T>(this Result<T> result, Func<T, bool> predicate, string errorMessage)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        if (!result.IsSuccess)
            return result;

        return predicate(result.Value!)
            ? result
            : Result<T>.Fail(errorMessage);
    }

    /// <summary>
    /// Executes a side effect when the result is successful. Returns the original result.
    /// </summary>
    public static Result<T> OnSuccess<T>(this Result<T> result, Action<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (result.IsSuccess)
            action(result.Value!);

        return result;
    }

    /// <summary>
    /// Executes a side effect when the result is a failure. Returns the original result.
    /// </summary>
    public static Result<T> OnFailure<T>(this Result<T> result, Action<IReadOnlyList<string>> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (!result.IsSuccess)
            action(result.Errors);

        return result;
    }

    /// <summary>
    /// Transforms the value of a successful result asynchronously. Propagates failure unchanged.
    /// </summary>
    public static async Task<Result<TOut>> MapAsync<T, TOut>(
        this Result<T> result,
        Func<T, Task<TOut>> mapper)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        return result.IsSuccess
            ? Result<TOut>.Success(await mapper(result.Value!).ConfigureAwait(false))
            : PropagateFailure<T, TOut>(result);
    }

    /// <summary>
    /// Chains an async result-producing operation onto a successful result.
    /// </summary>
    public static async Task<Result<TOut>> BindAsync<T, TOut>(
        this Result<T> result,
        Func<T, Task<Result<TOut>>> binder)
    {
        ArgumentNullException.ThrowIfNull(binder);
        return result.IsSuccess
            ? await binder(result.Value!).ConfigureAwait(false)
            : PropagateFailure<T, TOut>(result);
    }

    /// <summary>
    /// Chains a synchronous transform onto a Task-wrapped result.
    /// </summary>
    public static async Task<Result<TOut>> ThenMap<T, TOut>(
        this Task<Result<T>> resultTask,
        Func<T, TOut> mapper)
    {
        var result = await resultTask.ConfigureAwait(false);
        return result.Map(mapper);
    }

    /// <summary>
    /// Chains an async binder onto a Task-wrapped result.
    /// </summary>
    public static async Task<Result<TOut>> ThenBind<T, TOut>(
        this Task<Result<T>> resultTask,
        Func<T, Task<Result<TOut>>> binder)
    {
        var result = await resultTask.ConfigureAwait(false);
        return await result.BindAsync(binder).ConfigureAwait(false);
    }

    private static Result<TOut> PropagateFailure<T, TOut>(Result<T> source)
    {
        var errors = source.Errors;

        return source.FailureType switch
        {
            ResultFailureType.Validation => Result<TOut>.ValidationFailure(errors),
            ResultFailureType.Unauthorized => Result<TOut>.Unauthorized(JoinErrors(errors)),
            ResultFailureType.Forbidden => Result<TOut>.Forbidden(JoinErrors(errors)),
            ResultFailureType.ContentBlocked => Result<TOut>.ContentBlocked(JoinErrors(errors)),
            ResultFailureType.NotFound => Result<TOut>.NotFound(JoinErrors(errors)),
            _ => Result<TOut>.Fail(errors.ToArray())
        };
    }

    private static string JoinErrors(IReadOnlyList<string> errors) =>
        errors.Count > 0 ? string.Join("; ", errors) : "Unknown error";
}
