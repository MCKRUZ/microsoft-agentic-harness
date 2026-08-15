using Application.Common.Logging;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;

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
    /// These are <em>defaults</em>, not overrides: registered via <c>PostConfigure&lt;LoggerFilterOptions&gt;</c>,
    /// which the Options pattern guarantees runs after every <c>Configure&lt;LoggerFilterOptions&gt;</c>
    /// call — including a host's own <c>Logging</c> configuration-section binding, when the host
    /// binds one. Each rule is added only if no <em>global</em> (provider-unscoped) rule already
    /// targets that exact category name, so a consumer's own <c>Logging:LogLevel:Azure</c> /
    /// <c>Logging:LogLevel:Azure.Identity</c> configuration — or an earlier code-registered filter —
    /// is left in place rather than silently replaced. A bare <see cref="IServiceCollection"/> with
    /// no configuration source bound has nothing to override in the first place; the defaults simply
    /// apply.
    /// </para>
    /// <para>
    /// <strong>OpenTelemetry export is filtered separately, on purpose:</strong> the OTel logging
    /// pipeline registers its own provider-scoped rule (<c>AddFilter&lt;OpenTelemetryLoggerProvider&gt;</c>,
    /// with <c>CategoryName = null</c>) for its export minimum level. .NET's <c>LoggerRuleSelector</c>
    /// treats any provider-scoped rule as strictly better than a category-only rule when selecting
    /// for that provider — so the global "Azure"/"Azure.Identity" rules above never apply to the OTel
    /// export sink at all, regardless of specificity or registration order, and Azure SDK diagnostics
    /// (which can carry resource identifiers, e.g. Key Vault secret names in request URIs) would
    /// otherwise reach exported telemetry unfiltered. This method adds matching rules scoped
    /// explicitly to <see cref="OpenTelemetryLoggerProvider"/> to close that gap; they are always
    /// applied (not gated by the "already configured" check above) since nothing else in this
    /// codebase registers a provider-scoped rule for this exact provider/category pair.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddAzureIdentityDiagnostics(this IServiceCollection services)
    {
        services.AddLogging();

        services.PostConfigure<LoggerFilterOptions>(options =>
        {
            AddGlobalDefaultUnlessConfigured(options, "Azure", LogLevel.Warning);
            AddGlobalDefaultUnlessConfigured(options, "Azure.Identity", LogLevel.Information);

            var openTelemetryProvider = typeof(OpenTelemetryLoggerProvider).FullName;
            options.Rules.Add(new LoggerFilterRule(openTelemetryProvider, "Azure", LogLevel.Warning, null));
            options.Rules.Add(new LoggerFilterRule(openTelemetryProvider, "Azure.Identity", LogLevel.Information, null));
        });

        services.AddSingleton<AzureEventSourceLogForwarder>();
        services.AddHostedService<AzureIdentityLogForwarderHostedService>();

        return services;
    }

    private static void AddGlobalDefaultUnlessConfigured(LoggerFilterOptions options, string category, LogLevel level)
    {
        if (options.Rules.Any(r => r.ProviderName == null && r.CategoryName == category))
            return;

        options.Rules.Add(new LoggerFilterRule(providerName: null, categoryName: category, logLevel: level, filter: null));
    }
}
