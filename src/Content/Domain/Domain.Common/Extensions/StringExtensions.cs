namespace Domain.Common.Extensions;

/// <summary>
/// Extension methods for <see cref="string"/> manipulation.
/// </summary>
public static class StringExtensions
{
    /// <summary>
    /// Truncates a string to the specified maximum length, appending "..." if truncated.
    /// </summary>
    public static string Truncate(this string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length > maxLength
            ? string.Concat(value.AsSpan(0, maxLength), "...")
            : value;
    }
}
