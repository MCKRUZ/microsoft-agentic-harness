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
    /// The (category, level) pairs applied by <see cref="AddAzureIdentityDiagnostics"/> — a
    /// single source of truth shared by the global rule set and the
    /// <see cref="OpenTelemetryLoggerProvider"/>-scoped rule set, so the two can never drift
    /// out of sync with each other.
    /// </summary>
    private static readonly (string Category, LogLevel Level)[] DefaultCategoryLevels =
    [
        ("Azure", LogLevel.Warning),
        ("Azure.Identity", LogLevel.Information),
    ];

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
    /// These are <em>defaults</em>, not overrides, for both the global rules and the
    /// OpenTelemetry-scoped rules below: registered via <c>PostConfigure&lt;LoggerFilterOptions&gt;</c>,
    /// which the Options pattern guarantees runs after every <c>Configure&lt;LoggerFilterOptions&gt;</c>
    /// call — including a host's own <c>Logging</c> configuration-section binding, when the host
    /// binds one. Each rule is added only if no rule already targets that exact
    /// (provider, category) pair, so a consumer's own <c>Logging:LogLevel:Azure</c> /
    /// <c>Logging:LogLevel:Azure.Identity</c> configuration, or an earlier code-registered filter
    /// — global or provider-scoped — is left in place rather than silently replaced in either
    /// direction (more permissive or more restrictive). A bare <see cref="IServiceCollection"/>
    /// with no configuration source bound has nothing to override in the first place; the
    /// defaults simply apply.
    /// </para>
    /// <para>
    /// <strong>OpenTelemetry export is filtered separately, on purpose:</strong> the OTel logging
    /// pipeline registers its own provider-scoped rule (<c>AddFilter&lt;OpenTelemetryLoggerProvider&gt;</c>,
    /// with <c>CategoryName = null</c>) for its export minimum level. .NET's <c>LoggerRuleSelector</c>
    /// treats any provider-scoped rule as strictly better than a category-only rule when selecting
    /// for that provider — so the global "Azure"/"Azure.Identity" rules above never apply to the OTel
    /// export sink at all, regardless of specificity or registration order, and Azure SDK diagnostics
    /// (which can carry resource identifiers, e.g. Key Vault secret names in request URIs) would
    /// otherwise reach exported telemetry unfiltered. This method adds matching default rules scoped
    /// explicitly to <see cref="OpenTelemetryLoggerProvider"/> to close that gap.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddAzureIdentityDiagnostics(this IServiceCollection services)
    {
        services.AddLogging();

        services.PostConfigure<LoggerFilterOptions>(options =>
        {
            var openTelemetryProvider = typeof(OpenTelemetryLoggerProvider).FullName;

            foreach (var (category, level) in DefaultCategoryLevels)
            {
                AddDefaultUnlessConfigured(options, providerName: null, category, level);
                AddDefaultUnlessConfigured(options, openTelemetryProvider, category, level);
            }
        });

        services.AddSingleton<AzureEventSourceLogForwarder>();
        services.AddHostedService<AzureIdentityLogForwarderHostedService>();

        return services;
    }

    private static void AddDefaultUnlessConfigured(
        LoggerFilterOptions options, string? providerName, string category, LogLevel level)
    {
        if (options.Rules.Any(r => r.ProviderName == providerName && r.CategoryName == category))
            return;

        options.Rules.Add(new LoggerFilterRule(providerName, category, level, filter: null));
    }
}
