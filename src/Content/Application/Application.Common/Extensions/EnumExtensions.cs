using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;

namespace Application.Common.Extensions;

/// <summary>
/// Extension methods for extracting <see cref="DescriptionAttribute"/> values from enum members.
/// Returns <see cref="string.Empty"/> when no attribute is present.
/// </summary>
/// <example>
/// <code>
/// public enum OrderStatus
/// {
///     [Description("Order received and being processed")]
///     Pending,
///     [Description("Order has been shipped")]
///     Shipped
/// }
///
/// var status = OrderStatus.Shipped;
/// string desc = status.ToDescriptionString(); // "Order has been shipped"
/// </code>
/// </example>
public static class EnumExtensions
{
    // Reflection results are cached per enum value to avoid repeated GetField/GetCustomAttribute
    // calls on hot paths. Follows the same ConcurrentDictionary caching pattern as
    // ReflectionHelper and ResultHelper.
    private static readonly ConcurrentDictionary<Enum, string> DescriptionCache = new();

    /// <summary>
    /// Retrieves the <see cref="DescriptionAttribute"/> value from an enum member,
    /// or <see cref="string.Empty"/> if no attribute is applied.
    /// </summary>
    /// <param name="value">The enum value to get the description for.</param>
    /// <returns>The description text, or <see cref="string.Empty"/>.</returns>
    public static string ToDescriptionString(this Enum value) =>
        DescriptionCache.GetOrAdd(value, static v =>
        {
            var field = v.GetType().GetField(v.ToString());
            if (field is null)
                return string.Empty;

            var attribute = field.GetCustomAttribute<DescriptionAttribute>();
            return attribute?.Description ?? string.Empty;
        });
}
