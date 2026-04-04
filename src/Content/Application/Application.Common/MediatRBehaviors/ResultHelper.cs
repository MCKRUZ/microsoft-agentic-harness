using System.Collections.Concurrent;
using System.Reflection;
using Domain.Common;

namespace Application.Common.MediatRBehaviors;

/// <summary>
/// Shared utility for pipeline behaviors that need to return <see cref="Result{T}"/> failures
/// instead of throwing exceptions. Caches reflection lookups per <c>(Type, MethodName)</c> pair
/// so repeated calls for the same <c>TResponse</c> incur zero reflection cost.
/// </summary>
internal static class ResultHelper
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
