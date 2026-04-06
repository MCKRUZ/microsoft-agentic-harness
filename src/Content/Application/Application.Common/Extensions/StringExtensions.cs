namespace Application.Common.Extensions;

/// <summary>
/// Extension methods for <see cref="string"/> manipulation.
/// </summary>
public static class StringExtensions
{
    /// <summary>
    /// Truncates a string to the specified maximum length, appending "..." if truncated.
    /// </summary>
    /// <param name="value">The string to truncate.</param>
    /// <param name="maxLength">The maximum allowed length before truncation.</param>
    /// <returns>
    /// The original string if within limits; otherwise the first
    /// <paramref name="maxLength"/> characters followed by "...".
    /// </returns>
    public static string Truncate(this string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length > maxLength
            ? string.Concat(value.AsSpan(0, maxLength), "...")
            : value;
    }
}
