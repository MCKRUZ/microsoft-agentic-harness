using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace Application.Common.Logging;

/// <summary>
/// A <see cref="ConsoleFormatter"/> that renders execution-aware log entries with
/// identity prefixes, step boundary markers, operation indentation,
/// and optional context indicators.
/// </summary>
/// <remarks>
/// This formatter reads <see cref="ExecutionScope"/> from the scope stack to produce
/// output like:
/// <code>
/// 14:32:01.123 INFO [planner] Starting step 3
/// 14:32:01.456 INFO [planner]   >> op:file_system Reading config.json
/// 14:32:01.789 WARN [main>research] Context: 12.5k/128k
/// </code>
/// Each executor gets a stable color via <see cref="LoggingHelper.GetStableExecutorColor"/>.
/// Register via <c>builder.AddConsole(o =&gt; o.FormatterName = "execution")</c>
/// and <c>builder.AddConsoleFormatter&lt;ExecutionConsoleFormatter, ConsoleFormatterOptions&gt;()</c>.
/// </remarks>
public sealed class ExecutionConsoleFormatter : ConsoleFormatter
{
    /// <summary>The formatter name used for registration.</summary>
    public const string FormatterName = "execution";

    /// <summary>
    /// Initializes a new instance of the <see cref="ExecutionConsoleFormatter"/> class.
    /// </summary>
    public ExecutionConsoleFormatter() : base(FormatterName)
    {
    }

    /// <inheritdoc />
    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
        if (message is null)
            return;

        var executionScope = ExecutionScopeProvider.GetCurrentScope(scopeProvider);
        var timestamp = DateTimeOffset.UtcNow;
        var levelColor = LoggingHelper.GetLevelColor(logEntry.LogLevel);
        var shortLevel = LoggingHelper.GetShortLevel(logEntry.LogLevel);

        textWriter.Write(AnsiColors.Gray);
        textWriter.Write(timestamp.ToString("HH:mm:ss.fff"));
        textWriter.Write(AnsiColors.Reset);
        textWriter.Write(' ');

        textWriter.Write(levelColor);
        textWriter.Write(shortLevel);
        textWriter.Write(AnsiColors.Reset);
        textWriter.Write(' ');

        if (executionScope?.ExecutorId is not null)
        {
            var displayName = LoggingHelper.GetExecutorDisplayName(
                executionScope.ExecutorId, executionScope.ParentExecutorId);
            var executorColor = LoggingHelper.GetStableExecutorColor(executionScope.ExecutorId);

            textWriter.Write(executorColor);
            textWriter.Write('[');
            textWriter.Write(displayName);
            textWriter.Write(']');
            textWriter.Write(AnsiColors.Reset);
            textWriter.Write(' ');
        }
        else
        {
            var shortCategory = LoggingHelper.GetShortCategory(logEntry.Category);
            textWriter.Write(AnsiColors.Cyan);
            textWriter.Write('[');
            textWriter.Write(shortCategory);
            textWriter.Write(']');
            textWriter.Write(AnsiColors.Reset);
            textWriter.Write(' ');
        }

        if (executionScope?.OperationName is not null)
        {
            textWriter.Write(AnsiColors.Gray);
            textWriter.Write("  >> op:");
            textWriter.Write(executionScope.OperationName);
            textWriter.Write(' ');
            textWriter.Write(AnsiColors.Reset);
        }

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
}
