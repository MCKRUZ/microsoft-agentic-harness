using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace Application.Common.Logging;

/// <summary>
/// A <see cref="ConsoleFormatter"/> that renders log entries with ANSI color codes
/// for improved readability in terminal environments. Colors are assigned based on
/// log level and logger category.
/// </summary>
/// <remarks>
/// This is the general-purpose console formatter. For execution-aware formatting with
/// identity prefixes, step boundaries, and operation indentation, use
/// <see cref="ExecutionConsoleFormatter"/> instead.
/// <para>
/// Register via <c>builder.AddConsole(o =&gt; o.FormatterName = "colorful")</c>
/// and <c>builder.AddConsoleFormatter&lt;ColorfulConsoleFormatter, ConsoleFormatterOptions&gt;()</c>.
/// </para>
/// </remarks>
public sealed class ColorfulConsoleFormatter : ConsoleFormatter
{
    /// <summary>The formatter name used for registration.</summary>
    public const string FormatterName = "colorful";

    /// <summary>
    /// Initializes a new instance of the <see cref="ColorfulConsoleFormatter"/> class.
    /// </summary>
    public ColorfulConsoleFormatter() : base(FormatterName)
    {
    }

    /// <inheritdoc />
    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
        if (message is null)
            return;
        var timestamp = DateTimeOffset.UtcNow;
        var levelColor = LoggingHelper.GetLevelColor(logEntry.LogLevel);
        var shortLevel = LoggingHelper.GetShortLevel(logEntry.LogLevel);
        var shortCategory = LoggingHelper.GetShortCategory(logEntry.Category);

        textWriter.Write(AnsiColors.Gray);
        textWriter.Write(timestamp.ToString("HH:mm:ss.fff"));
        textWriter.Write(AnsiColors.Reset);
        textWriter.Write(' ');

        textWriter.Write(levelColor);
        textWriter.Write(shortLevel);
        textWriter.Write(AnsiColors.Reset);
        textWriter.Write(' ');

        textWriter.Write(AnsiColors.Cyan);
        textWriter.Write('[');
        textWriter.Write(shortCategory);
        textWriter.Write(']');
        textWriter.Write(AnsiColors.Reset);
        textWriter.Write(' ');

        WriteScopeInformation(textWriter, scopeProvider);

        textWriter.Write(message);
        textWriter.WriteLine();

        if (logEntry.Exception is not null)
        {
            textWriter.Write(AnsiColors.Red);
            textWriter.Write(logEntry.Exception.ToString());
            textWriter.Write(AnsiColors.Reset);
            textWriter.WriteLine();
        }
    }

    private static void WriteScopeInformation(TextWriter textWriter, IExternalScopeProvider? scopeProvider)
    {
        if (scopeProvider is null)
            return;

        scopeProvider.ForEachScope(static (scope, writer) =>
        {
            if (scope is null)
                return;

            writer.Write(AnsiColors.Gray);
            writer.Write("=> ");
            writer.Write(scope);
            writer.Write(' ');
            writer.Write(AnsiColors.Reset);
        }, textWriter);
    }
}
