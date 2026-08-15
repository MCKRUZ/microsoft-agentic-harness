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
    /// "DefaultAzureCredential credential selected: {0}" line this exists for.
    /// </para>
    /// <para>
    /// <strong>Not overridable from <c>appsettings.json</c>:</strong> these two filters are
    /// registered here, in code, after the host's own configuration-bound logging filters — and
    /// .NET's rule selection picks the last-registered rule when two rules match a category with
    /// equal specificity. An operator setting <c>Logging:LogLevel:Azure</c> in configuration will
    /// not change the effective level; this method's <see cref="LogLevel.Warning"/> always wins.
    /// That's the deliberate, safer direction — it can't be accidentally relaxed — but it means a
    /// genuine need for deeper Azure SDK tracing has to go through the full EventSource listener
    /// this class exists to make unnecessary for the common case, not through configuration.
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
