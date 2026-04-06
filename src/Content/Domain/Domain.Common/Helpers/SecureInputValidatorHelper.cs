using System.Text.RegularExpressions;

namespace Domain.Common.Helpers;

/// <summary>
/// Validates untrusted string inputs at system boundaries. Rejects inputs
/// containing shell injection, path traversal, or injection patterns.
/// </summary>
public static class SecureInputValidatorHelper
{
    private const int DefaultMaxLength = 1024;
    private const int MaxPathLength = 4096;

    private static readonly Regex SafeGeneralPattern = new(
        @"^[a-zA-Z0-9\s\-_.:,/\\@T=+()#\[\]{}""'!?]+$",
        RegexOptions.Compiled);

    private static readonly Regex IdentifierPattern = new(
        @"^[a-zA-Z0-9\-_.:]+$",
        RegexOptions.Compiled);

    private static readonly char[] ShellMetaChars = [';', '|', '`', '$', '>', '<'];

    private static readonly string[] ShellInjectionPatterns =
        ["&&", "||", "$(", "$()", ">>", "<<"];

    private static readonly string[] PathTraversalPatterns =
        ["..", "~/", "~\\", "%2e%2e", "%2f", "%5c"];

    /// <summary>
    /// Validates a general-purpose string input against injection patterns.
    /// </summary>
    public static bool ValidateInput(string input, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrWhiteSpace(input))
            return false;

        if (input.Length > maxLength)
            return false;

        if (ContainsShellInjection(input))
            return false;

        if (!SafeGeneralPattern.IsMatch(input))
            return false;

        return true;
    }

    /// <summary>
    /// Validates a file path argument. Checks for path traversal,
    /// shell injection, null bytes, and length limits.
    /// </summary>
    public static bool ValidateFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (path.Length > MaxPathLength)
            return false;

        if (path.Contains('\0'))
            return false;

        if (ContainsPathTraversal(path))
            return false;

        if (ContainsShellInjection(path))
            return false;

        return true;
    }

    /// <summary>
    /// Validates an identifier (tool name, agent ID, etc.). Must be alphanumeric
    /// with hyphens, underscores, dots, and colons only.
    /// </summary>
    public static bool ValidateIdentifier(string identifier, int maxLength = 128)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            return false;

        if (identifier.Length > maxLength)
            return false;

        return IdentifierPattern.IsMatch(identifier);
    }

    /// <summary>
    /// Returns a sanitized version of the input by removing dangerous characters.
    /// </summary>
    public static string Sanitize(string input, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        var length = Math.Min(input.Length, maxLength);
        var buffer = length <= 256 ? stackalloc char[length] : new char[length];
        var pos = 0;

        foreach (var c in input)
        {
            if (pos >= length)
                break;

            if (char.IsControl(c) && c is not (' ' or '\t' or '\n' or '\r'))
                continue;

            if (Array.IndexOf(ShellMetaChars, c) >= 0)
                continue;

            buffer[pos++] = c;
        }

        return new string(buffer[..pos]);
    }

    private static bool ContainsShellInjection(string input)
    {
        if (input.AsSpan().IndexOfAny(ShellMetaChars) >= 0)
            return true;

        foreach (var pattern in ShellInjectionPatterns)
        {
            if (input.Contains(pattern, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool ContainsPathTraversal(string input)
    {
        foreach (var pattern in PathTraversalPatterns)
        {
            if (input.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
