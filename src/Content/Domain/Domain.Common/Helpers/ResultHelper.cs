using System.Collections.Concurrent;
using System.Reflection;

namespace Domain.Common.Helpers;

/// <summary>
/// Shared utility for creating <see cref="Result{T}"/> failures via reflection.
/// Caches reflection lookups per (Type, MethodName) pair for zero-cost repeated calls.
/// </summary>
public static class ResultHelper
{
    private static readonly ConcurrentDictionary<(Type, string), MethodInfo?> MethodCache = new();

    /// <summary>
    /// Attempts to create a failure result of type <typeparamref name="TResponse"/>
    /// by invoking the named static factory method on the type.
    /// </summary>
    public static bool TryCreateFailure<TResponse>(
        string factoryMethodName,
        string reason,
        out TResponse result)
    {
        result = default!;

        var method = MethodCache.GetOrAdd(
            (typeof(TResponse), factoryMethodName),
            static key => !typeof(Result).IsAssignableFrom(key.Item1)
                ? null
                : key.Item1.GetMethod(
                    key.Item2,
                    BindingFlags.Public | BindingFlags.Static,
                    [typeof(string)]));

        if (method is null)
            return false;

        result = (TResponse)method.Invoke(null, [reason])!;
        return true;
    }

    /// <summary>
    /// Attempts to create a validation failure result of type <typeparamref name="TResponse"/>
    /// by invoking <c>ValidationFailure(IReadOnlyList&lt;string&gt;)</c>.
    /// </summary>
    public static bool TryCreateValidationFailure<TResponse>(
        IReadOnlyList<string> errors,
        out TResponse result)
    {
        result = default!;

        var method = MethodCache.GetOrAdd(
            (typeof(TResponse), nameof(Result.ValidationFailure)),
            static key => !typeof(Result).IsAssignableFrom(key.Item1)
                ? null
                : key.Item1.GetMethod(
                    key.Item2,
                    BindingFlags.Public | BindingFlags.Static,
                    [typeof(IReadOnlyList<string>)]));

        if (method is null)
            return false;

        result = (TResponse)method.Invoke(null, [errors])!;
        return true;
    }
}
