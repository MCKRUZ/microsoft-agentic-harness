namespace Application.Common.Logging;

/// <summary>
/// ANSI color codes used by console formatters.
/// </summary>
public static class AnsiColors
{
    /// <summary>Reset to default terminal color.</summary>
    public const string Reset = "\x1B[39m\x1B[22m";
    /// <summary>Red — used for Error level.</summary>
    public const string Red = "\x1B[31m";
    /// <summary>Yellow — used for Warning level.</summary>
    public const string Yellow = "\x1B[33m";
    /// <summary>Green — used for Information level.</summary>
    public const string Green = "\x1B[32m";
    /// <summary>Cyan — used for Debug level.</summary>
    public const string Cyan = "\x1B[36m";
    /// <summary>Gray — used for Trace level and muted content.</summary>
    public const string Gray = "\x1B[90m";
    /// <summary>Magenta — used for Critical level.</summary>
    public const string Magenta = "\x1B[35m";
    /// <summary>Blue — used for executor identity.</summary>
    public const string Blue = "\x1B[34m";
    /// <summary>Bold modifier.</summary>
    public const string Bold = "\x1B[1m";
}
