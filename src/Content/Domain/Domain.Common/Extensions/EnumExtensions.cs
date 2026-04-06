using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;

namespace Domain.Common.Extensions;

/// <summary>
/// Extension methods for extracting <see cref="DescriptionAttribute"/> values from enum members.
/// </summary>
public static class EnumExtensions
{
    private static readonly ConcurrentDictionary<Enum, string> DescriptionCache = new();

    /// <summary>
    /// Retrieves the <see cref="DescriptionAttribute"/> value from an enum member,
    /// or <see cref="string.Empty"/> if no attribute is applied.
    /// </summary>
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
