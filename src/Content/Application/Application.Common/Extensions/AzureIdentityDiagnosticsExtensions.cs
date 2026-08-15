using Application.Common.Logging;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Application.Common.Extensions;

/// <summary>
/// Extension methods for wiring Azure SDK diagnostics into the harness's logging pipeline.
/// </summary>
public static class AzureIdentityDiagnosticsExtensions
{
    /// <summary>
    /// Bridges Azure SDK EventSource diagnostics into <see cref="ILogger"/>, so which credential
    /// <c>DefaultAzureCredential</c> selected is visible at normal log-Information level instead
    /// of requiring full EventSource tracing.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// <para>
    /// <see cref="AzureEventSourceLogForwarder"/> (from <c>Microsoft.Extensions.Azure</c>) always
    /// listens at <c>EventLevel.Verbose</c> internally, so every Azure SDK EventSource in the
    /// process — not just Azure Identity — starts flowing into <see cref="ILogger"/> once it
    /// starts. Azure.Core's own per-HTTP-call logging is noisy at Information level across every
    /// Azure SDK this template uses, so the <c>Azure</c> category defaults to
    /// <see cref="LogLevel.Warning"/> here, with <c>Azure.Identity</c> carved back out to
    /// <see cref="LogLevel.Information"/> — the one category carrying the
    /// "DefaultAzureCredential credential selected: {0}" line this exists for. Consumers can
    /// still override either category from configuration.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddAzureIdentityDiagnostics(this IServiceCollection services)
    {
        services.AddLogging(builder =>
        {
            builder.AddFilter("Azure", LogLevel.Warning);
            builder.AddFilter("Azure.Identity", LogLevel.Information);
        });

        services.AddSingleton<AzureEventSourceLogForwarder>();
        services.AddHostedService<AzureIdentityLogForwarderHostedService>();

        return services;
    }
}
