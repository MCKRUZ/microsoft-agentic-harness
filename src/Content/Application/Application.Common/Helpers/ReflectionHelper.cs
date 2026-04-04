using System.Collections.Concurrent;
using System.Reflection;

namespace Application.Common.Helpers;

/// <summary>
/// Stateless reflection utilities for dynamic property access.
/// Supports nested dot-notation paths (e.g., <c>"Agent.Config.Timeout"</c>).
/// PropertyInfo lookups are cached per (Type, property name) pair.
/// </summary>
/// <remarks>
/// Used for generic logging, diagnostics, and dynamic configuration scenarios
/// where compile-time property access is not available.
/// </remarks>
public static class ReflectionHelper
{
    private static readonly ConcurrentDictionary<(Type, string), PropertyInfo?> PropertyCache = new();
    /// <summary>
    /// Retrieves a property value from an object by name, supporting nested
    /// dot-notation paths.
    /// </summary>
    /// <param name="source">The object to read from.</param>
    /// <param name="propertyPath">
    /// The property name or dot-separated path (e.g., <c>"Name"</c> or <c>"Agent.Config.Timeout"</c>).
    /// </param>
    /// <returns>The property value, or <c>null</c> if the property is not found or any segment is null.</returns>
    /// <example>
    /// <code>
    /// var config = new { Agent = new { Name = "planner", Timeout = 30 } };
    /// var name = ReflectionHelper.GetPropertyValue(config, "Agent.Name"); // "planner"
    /// var missing = ReflectionHelper.GetPropertyValue(config, "Agent.Missing"); // null
    /// </code>
    /// </example>
    public static object? GetPropertyValue(object? source, string propertyPath)
    {
        if (source is null || string.IsNullOrWhiteSpace(propertyPath))
            return null;

        var current = source;

        foreach (var segment in propertyPath.Split('.'))
        {
            if (current is null)
                return null;

            var property = PropertyCache.GetOrAdd(
                (current.GetType(), segment),
                static key => key.Item1.GetProperty(key.Item2));
            if (property is null)
                return null;

            current = property.GetValue(current);
        }

        return current;
    }
}
