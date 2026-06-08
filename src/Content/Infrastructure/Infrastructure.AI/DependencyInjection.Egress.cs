using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Egress;
using Application.AI.Common.Interfaces.Skills;
using Domain.AI.Egress;
using Domain.Common.Config;
using Infrastructure.AI.Egress;
using Infrastructure.AI.Skills;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI;

public static partial class DependencyInjection
{
    /// <summary>
    /// Registers the egress layer (PR-3b): per-skill outbound allowlist policy,
    /// SSRF defense via <c>Microsoft.Security.AntiSSRF</c>, JSONL audit, and the
    /// named <see cref="HttpClient"/> ("egress") that composes the outer
    /// <see cref="EgressPolicyDelegatingHandler"/> above the inner
    /// AntiSSRF terminal handler.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All registrations are unconditional — the named <see cref="HttpClient"/>
    /// always registers; the layer is inert when <c>AppConfig.AI.Egress.Enabled</c>
    /// is false (no consumer routes traffic through it and the startup
    /// validator skips its checks).
    /// </para>
    /// <para>
    /// Handler chain composition (outer → inner):
    /// </para>
    /// <list type="number">
    ///   <item><description><see cref="EgressPolicyDelegatingHandler"/> — resolves identity, runs the per-skill allowlist policy, throws on deny, audits every decision.</description></item>
    ///   <item><description><see cref="AntiSsrfHandlerFactory"/> produces the terminal <c>AntiSSRFHandler</c> — connect-time IP filtering, IMDS deny, redirect re-validation.</description></item>
    /// </list>
    /// </remarks>
    private static void RegisterEgressServices(IServiceCollection services)
    {
        // --- Audit ---
        services.AddSingleton<IEgressAuditWriter, JsonlEgressAuditWriter>();

        // --- Default policy (configuration-bound) + resolver ---
        services.AddSingleton<IEgressPolicy>(sp =>
        {
            var config = sp.GetRequiredService<IOptionsMonitor<AppConfig>>().CurrentValue;
            var entries = EgressAllowlistMapper.Map(config.AI.Egress.DefaultAllowlist);
            var logger = sp.GetRequiredService<ILogger<DefaultEgressPolicy>>();
            var timeProvider = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            return new DefaultEgressPolicy(entries, logger, timeProvider);
        });

        // --- Per-skill resolver (PR-3c) replaces the PR-3b default resolver ---
        // ICurrentSkillAccessor is AsyncLocal-backed → singleton (per-flow, not per-scope).
        services.AddSingleton<ICurrentSkillAccessor, CurrentSkillAccessor>();
        services.AddSingleton<IEgressPolicyResolver, SkillManifestEgressPolicyResolver>();

        // --- AntiSSRF terminal handler factory ---
        services.AddSingleton<AntiSsrfHandlerFactory>();

        // --- Named HttpClient with outer DelegatingHandler + inner AntiSSRF primary handler ---
        services.AddTransient<EgressPolicyDelegatingHandler>(sp =>
        {
            return new EgressPolicyDelegatingHandler(
                sp,
                sp.GetRequiredService<IAmbientRequestScope>(),
                sp.GetRequiredService<IEgressAuditWriter>(),
                sp.GetRequiredService<ILogger<EgressPolicyDelegatingHandler>>(),
                sp.GetService<TimeProvider>() ?? TimeProvider.System);
        });

        services.AddHttpClient(EgressPolicyDelegatingHandler.ClientName)
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var factory = sp.GetRequiredService<AntiSsrfHandlerFactory>();
                return factory.GetOrCreate();
            })
            .AddHttpMessageHandler<EgressPolicyDelegatingHandler>();

        // --- Startup-time validator ---
        services.AddHostedService<EgressStartupValidator>();
    }
}
