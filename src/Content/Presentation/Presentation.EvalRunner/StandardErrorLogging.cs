using Application.Common.Extensions;
using Application.Common.Logging;
using Domain.Common.Config;
using Domain.Common.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Runner = Infrastructure.AI.Evaluation.Runners.EvalRunner;

namespace Presentation.EvalRunner;

/// <summary>
/// Sends error-level log entries, and the eval runner's pre-run warnings, to standard error when the host has console
/// logging switched off.
/// </summary>
/// <remarks>
/// <para>
/// The EvalRunner ships with <see cref="LoggingConfig.SuppressConsoleOutput"/> on, because a report written with
/// <c>--out console</c> owns standard output. Its file logs stay inert unless a log run is started, which this host
/// never does. Without this sink, the cause of an internal fault, which the turn handler logs and deliberately keeps
/// out of the returned error text, reached no place an operator could read (#1070).
/// </para>
/// <para>
/// Errors are written, plus the runner's <see cref="Runner.PreRunWarningEvent"/> warnings (an agent the host does
/// not have, an override key nothing reads), which the report would otherwise show only after every case has run
/// (#1071). Other warnings stay out: they include one per tool a host cannot build, on every agent build, and the
/// runner's per-case ones repeat an error already written. The entries pass through the same redacting logger factory
/// as every other local sink, so a secret in an exception's text is redacted here too. With console logging on, errors
/// already reach the console, so nothing is added.
/// </para>
/// </remarks>
internal static class StandardErrorLogging
{
    /// <summary>
    /// Adds an error-only log sink that writes to <paramref name="writer"/>, when
    /// <paramref name="logging"/> suppresses console output.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="logging">The host's logging configuration.</param>
    /// <param name="writer">Where entries go; the host passes <see cref="Console.Error"/>.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddErrorsToStandardError(
        this IServiceCollection services, LoggingConfig logging, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logging);
        ArgumentNullException.ThrowIfNull(writer);

        if (!logging.SuppressConsoleOutput)
            return services;

        return services.AddLogging(builder => builder
            .AddCallback(entry => Write(writer, entry))
            .AddFilter<CallbackLoggerProvider>(category: null, LogLevel.Error)
            .AddFilter<CallbackLoggerProvider>(typeof(Runner).FullName, LogLevel.Warning));
    }

    // Cases can run in parallel, so lines from two failures must not interleave.
    private static void Write(TextWriter writer, LogEntry entry)
    {
        if (entry.Level < LogLevel.Error && entry.EventId != Runner.PreRunWarningEvent)
            return;

        lock (writer)
        {
            writer.WriteLine($"{entry.Level}: {entry.Category}: {entry.Message}");
            if (entry.Exception is not null)
                writer.WriteLine(entry.Exception.ToString());
        }
    }
}
