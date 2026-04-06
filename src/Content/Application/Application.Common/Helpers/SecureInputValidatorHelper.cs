using System.Text.RegularExpressions;

namespace Application.Common.Helpers;

/// <summary>
/// Validates untrusted string inputs at system boundaries — specifically LLM-generated
/// tool arguments, user-supplied prompts, and MCP request parameters. Rejects inputs
/// containing shell injection, path traversal, or prompt injection patterns.
/// </summary>
/// <remarks>
/// <para>
/// This is a <strong>boundary guard</strong>, not a replacement for parameterized queries
/// or OS-level sandboxing. It catches the most common injection vectors before they reach
/// tool execution, providing defense-in-depth alongside tool permission checks
/// (<see cref="Interfaces.Agent.IToolPermissionService"/>).
/// </para>
/// <para>
/// All methods are static and thread-safe. Regex patterns are compiled once at class load.
/// </para>
/// </remarks>
public static class SecureInputValidatorHelper
{
    /// <summary>Default maximum input length for general validation.</summary>
    private const int DefaultMaxLength = 1024;

    /// <summary>Maximum length for file path inputs (Linux PATH_MAX, covers Windows 260 too).</summary>
    private const int MaxPathLength = 4096;

    // Alphanumeric + common safe punctuation. Intentionally excludes shell
    // metacharacters and backticks.
    private static readonly Regex SafeGeneralPattern = new(
        @"^[a-zA-Z0-9\s\-_.:,/\\@T=+()#\[\]{}""'!?]+$",
        RegexOptions.Compiled);

    // Tool names, skill IDs, agent identifiers — strict alphanumeric subset.
    private static readonly Regex IdentifierPattern = new(
        @"^[a-zA-Z0-9\-_.:]+$",
        RegexOptions.Compiled);

    // Single dangerous characters for fast IndexOfAny short-circuit.
    private static readonly char[] ShellMetaChars = [';', '|', '`', '$', '>', '<'];

    // Multi-char patterns checked only after single-char match.
    private static readonly string[] ShellInjectionPatterns =
        ["&&", "||", "$(", "$()", ">>", "<<"];

    // Patterns that indicate path traversal attempts.
    private static readonly string[] PathTraversalPatterns =
        ["..", "~/" , "~\\", "%2e%2e", "%2f", "%5c"];

    #region Public Methods

    /// <summary>
    /// Validates a general-purpose string input against injection patterns.
    /// Use for tool arguments, search queries, and user-supplied text.
    /// </summary>
    /// <param name="input">The input string to validate.</param>
    /// <param name="maxLength">Maximum allowed length. Default: 1024.</param>
    /// <returns><c>true</c> if the input is safe; <c>false</c> if rejected.</returns>
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
    /// Validates a file path argument from an LLM tool call. Checks for path traversal,
    /// shell injection, null bytes, and length limits.
    /// </summary>
    /// <param name="path">The file path to validate.</param>
    /// <returns><c>true</c> if the path is safe; <c>false</c> if rejected.</returns>
    public static bool ValidateFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (path.Length > MaxPathLength)
            return false;

        // Null bytes can truncate paths in native code
        if (path.Contains('\0'))
            return false;

        if (ContainsPathTraversal(path))
            return false;

        if (ContainsShellInjection(path))
            return false;

        return true;
    }

    /// <summary>
    /// Validates a tool name or identifier. Must be alphanumeric with hyphens,
    /// underscores, dots, and colons only (e.g., "file_system", "mcp.server:tool-name").
    /// </summary>
    /// <param name="identifier">The identifier to validate.</param>
    /// <param name="maxLength">Maximum allowed length. Default: 128.</param>
    /// <returns><c>true</c> if the identifier is safe; <c>false</c> if rejected.</returns>
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
    /// Use when you need to log or display untrusted input rather than reject it.
    /// </summary>
    /// <param name="input">The input to sanitize.</param>
    /// <param name="maxLength">Maximum output length. Default: 1024.</param>
    /// <returns>The sanitized string, truncated if necessary.</returns>
    public static string Sanitize(string input, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        // Single-pass: keep only safe characters (non-control + common whitespace),
        // exclude shell metacharacters. Avoids LINQ allocations and chained Replace.
        var length = Math.Min(input.Length, maxLength);
        var buffer = length <= 256 ? stackalloc char[length] : new char[length];
        var pos = 0;

        foreach (var c in input)
        {
            if (pos >= length)
                break;

            // Drop control characters (keep common whitespace)
            if (char.IsControl(c) && c is not (' ' or '\t' or '\n' or '\r'))
                continue;

            // Drop shell metacharacters
            if (Array.IndexOf(ShellMetaChars, c) >= 0)
                continue;

            buffer[pos++] = c;
        }

        return new string(buffer[..pos]);
    }

    #endregion

    #region Private Methods

    private static bool ContainsShellInjection(string input)
    {
        // Fast path: check single dangerous characters first
        if (input.AsSpan().IndexOfAny(ShellMetaChars) >= 0)
            return true;

        // Slow path: multi-char patterns
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

    #endregion
}
