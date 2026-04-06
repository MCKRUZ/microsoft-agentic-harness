using Application.Common.Logging;
using Application.Common.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace Application.Common.Extensions;

/// <summary>
/// Extension methods for <see cref="ILoggingBuilder"/> to register
/// agentic harness logging providers and formatters.
/// </summary>
public static class ILoggingBuilderExtensions
{
    /// <summary>
    /// Adds the named pipe logger for real-time log streaming to a separate viewer process.
    /// Pipe name is configured via <c>AppConfig.Logging.PipeName</c>.
    /// </summary>
    public static ILoggingBuilder AddNamedPipe(this ILoggingBuilder builder)
    {
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ILoggerProvider, NamedPipeLoggerProvider>());
        return builder;
    }

    /// <summary>
    /// Adds the file logger for persistent per-run log output in human-readable and
    /// structured formats. Log path is configured via <c>AppConfig.Logging.LogsBasePath</c>.
    /// </summary>
    public static ILoggingBuilder AddFileLogger(this ILoggingBuilder builder)
    {
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ILoggerProvider, FileLoggerProvider>());
        RegisterConcreteProvider<FileLoggerProvider>(builder.Services);

        return builder;
    }

    /// <summary>
    /// Adds the structured JSON logger for machine-parseable JSONL output per run.
    /// Enables session debugging, token accounting, and tool usage auditing.
    /// </summary>
    public static ILoggingBuilder AddStructuredJsonLogger(this ILoggingBuilder builder)
    {
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ILoggerProvider, StructuredJsonLoggerProvider>());
        RegisterConcreteProvider<StructuredJsonLoggerProvider>(builder.Services);

        return builder;
    }

    /// <summary>
    /// Adds the in-memory ring buffer logger for diagnostics endpoints.
    /// Buffer capacity is configured via <c>AppConfig.Logging.RingBufferCapacity</c>.
    /// </summary>
    public static ILoggingBuilder AddInMemoryRingBuffer(this ILoggingBuilder builder)
    {
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ILoggerProvider, InMemoryRingBufferLoggerProvider>());
        RegisterConcreteProvider<InMemoryRingBufferLoggerProvider>(builder.Services);

        return builder;
    }

    /// <summary>
    /// Adds a callback-based logger that invokes a delegate for each log entry.
    /// Designed for SDK consumers embedding the agent harness who want a simple
    /// lambda-based log handler.
    /// </summary>
    /// <param name="builder">The logging builder.</param>
    /// <param name="callback">
    /// The delegate invoked for each log entry. Keep it fast — runs synchronously
    /// on the logging thread.
    /// </param>
    public static ILoggingBuilder AddCallback(
        this ILoggingBuilder builder,
        Action<LogEntry> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ILoggerProvider>(
                new CallbackLoggerProvider(callback)));

        return builder;
    }

    /// <summary>
    /// Registers a concrete <see cref="ILoggerProvider"/> implementation as a singleton
    /// pointing to the same instance from the provider enumeration, enabling direct injection
    /// for lifecycle management (e.g., <c>StartNewRun</c>/<c>CompleteRun</c>).
    /// </summary>
    private static void RegisterConcreteProvider<T>(IServiceCollection services)
        where T : class, ILoggerProvider
    {
        services.AddSingleton(sp =>
            sp.GetServices<ILoggerProvider>().OfType<T>().First());
    }

    /// <summary>
    /// Registers the agent-aware console formatter that renders identity prefixes,
    /// turn boundary markers, and tool invocation indentation. Uses stable colors
    /// per agent for visual differentiation.
    /// </summary>
    public static ILoggingBuilder AddAgentConsoleFormatter(this ILoggingBuilder builder)
    {
        builder.AddConsoleFormatter<AgentConsoleFormatter, ConsoleFormatterOptions>();
        builder.AddConsole(options =>
            options.FormatterName = AgentConsoleFormatter.FormatterName);

        return builder;
    }
}
