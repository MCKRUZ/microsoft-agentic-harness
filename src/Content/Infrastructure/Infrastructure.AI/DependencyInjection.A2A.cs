using Application.AI.Common.Interfaces.A2A;
using Domain.Common.Config;
using Domain.Common.Config.AI.A2A;
using Infrastructure.AI.A2A;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI;

public static partial class DependencyInjection
{
    /// <summary>
    /// Registers the PR-7 A2A surface: client, server, span emitter, identity
    /// propagator, and the configured auth provider.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Auth provider selection runs at DI registration time, not per call: the
    /// in-process provider has no transport credentials to stamp; the
    /// cross-process provider acquires JWTs. Mixing them in a single process
    /// would either leak credentials onto in-process calls (waste + audit
    /// noise) or strip them from cross-process calls (auth failure). One
    /// provider per process.
    /// </para>
    /// <para>
    /// The cross-process provider depends on consumer-supplied
    /// <see cref="IA2ATokenAcquirer"/> + <see cref="IA2ATokenValidator"/>
    /// implementations. Consumers register those before calling this method,
    /// or the cross-process transport will fail-loud on first call.
    /// </para>
    /// </remarks>
    private static void RegisterA2AServices(IServiceCollection services)
    {
        // Span emitter owns the ActivitySource — single instance per process.
        services.AddSingleton<A2ASpanEmitter>();

        // Identity propagator is scoped — it reads ambient IAgentExecutionContext.
        services.AddScoped<A2AIdentityPropagator>();

        // Auth provider selection mirrors the configured transport. Scoped so
        // the in-process variant can capture the scoped IAgentExecutionContext;
        // the cross-process variant doesn't need scope but inherits the
        // lifetime for consistency.
        services.AddScoped<IA2AAuthenticationProvider>(sp =>
        {
            var transport = sp.GetRequiredService<IOptionsMonitor<AppConfig>>()
                .CurrentValue.AI.A2A.Surface.Transport;

            return transport switch
            {
                A2ATransport.Http => ActivatorUtilities
                    .CreateInstance<CrossProcessA2AAuthenticationProvider>(sp),
                _ => ActivatorUtilities
                    .CreateInstance<InProcessA2AAuthenticationProvider>(sp)
            };
        });

        // Server + client are scoped: they depend on scoped auth provider +
        // identity propagator.
        services.AddScoped<IA2AServer, HarnessA2AServer>();
        services.AddScoped<IA2AClient, HarnessA2AClient>();

        services.AddHttpClient(HarnessA2AClient.HttpClientName);
    }
}
