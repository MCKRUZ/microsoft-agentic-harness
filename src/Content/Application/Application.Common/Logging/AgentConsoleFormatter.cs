using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace Application.Common.Logging;

/// <summary>
/// A <see cref="ConsoleFormatter"/> that renders agent-aware log entries with
/// identity prefixes, turn boundary markers, tool invocation indentation,
/// and optional token budget indicators.
/// </summary>
/// <remarks>
/// This formatter reads <see cref="AgentLogScope"/> from the scope stack to produce
/// output like:
/// <code>
/// 14:32:01.123 INFO [planner] Starting turn 3
/// 14:32:01.456 INFO [planner]   >> tool:file_system Reading config.json
/// 14:32:01.789 WARN [main>research] Token budget: 12.5k/128k
/// </code>
/// Each agent gets a stable color via <see cref="LoggingHelper.GetStableAgentColor"/>.
/// Register via <c>builder.AddConsole(o =&gt; o.FormatterName = "agent")</c>
/// and <c>builder.AddConsoleFormatter&lt;AgentConsoleFormatter, ConsoleFormatterOptions&gt;()</c>.
/// </remarks>
public sealed class AgentConsoleFormatter : ConsoleFormatter
{
    /// <summary>The formatter name used for registration.</summary>
    public const string FormatterName = "agent";

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentConsoleFormatter"/> class.
    /// </summary>
    public AgentConsoleFormatter() : base(FormatterName)
    {
    }

    /// <inheritdoc />
    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
        if (message is null)
            return;

        var agentScope = AgentScopeProvider.GetCurrentAgentScope(scopeProvider);
        var timestamp = DateTimeOffset.UtcNow;
        var levelColor = LoggingHelper.GetLevelColor(logEntry.LogLevel);
        var shortLevel = LoggingHelper.GetShortLevel(logEntry.LogLevel);

        textWriter.Write(LoggingHelper.AnsiColors.Gray);
        textWriter.Write(timestamp.ToString("HH:mm:ss.fff"));
        textWriter.Write(LoggingHelper.AnsiColors.Reset);
        textWriter.Write(' ');

        textWriter.Write(levelColor);
        textWriter.Write(shortLevel);
        textWriter.Write(LoggingHelper.AnsiColors.Reset);
        textWriter.Write(' ');

        if (agentScope?.AgentId is not null)
        {
            var displayName = LoggingHelper.GetAgentDisplayName(
                agentScope.AgentId, agentScope.ParentAgentId);
            var agentColor = LoggingHelper.GetStableAgentColor(agentScope.AgentId);

            textWriter.Write(agentColor);
            textWriter.Write('[');
            textWriter.Write(displayName);
            textWriter.Write(']');
            textWriter.Write(LoggingHelper.AnsiColors.Reset);
            textWriter.Write(' ');
        }
        else
        {
            var shortCategory = LoggingHelper.GetShortCategory(logEntry.Category);
            textWriter.Write(LoggingHelper.AnsiColors.Cyan);
            textWriter.Write('[');
            textWriter.Write(shortCategory);
            textWriter.Write(']');
            textWriter.Write(LoggingHelper.AnsiColors.Reset);
            textWriter.Write(' ');
        }

        if (agentScope?.ToolName is not null)
        {
            textWriter.Write(LoggingHelper.AnsiColors.Gray);
            textWriter.Write("  >> tool:");
            textWriter.Write(agentScope.ToolName);
            textWriter.Write(' ');
            textWriter.Write(LoggingHelper.AnsiColors.Reset);
        }

        textWriter.Write(message);
        textWriter.WriteLine();

        if (logEntry.Exception is not null)
        {
            textWriter.Write(LoggingHelper.AnsiColors.Red);
            textWriter.Write(logEntry.Exception.ToString());
            textWriter.Write(LoggingHelper.AnsiColors.Reset);
            textWriter.WriteLine();
        }
    }
}
