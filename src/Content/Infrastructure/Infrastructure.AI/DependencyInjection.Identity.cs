using Application.AI.Common.Interfaces.Identity;
using Infrastructure.AI.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.AI;

public static partial class DependencyInjection
{
    /// <summary>
    /// Registers the agent-identity subsystem: five credential providers (one per
    /// <c>AgentIdentityKind</c>), the credential-hierarchy resolver, and the
    /// tool-level RBAC validator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All five providers register unconditionally. Each reports itself "not
    /// configured" at resolve time when its config sub-section is missing the
    /// required fields, so the resolver transparently walks to the next kind in
    /// the priority hierarchy. Registering all providers means consumers don't
    /// need to know in advance which credential kinds their environment will use
    /// — config alone drives behaviour at runtime.
    /// </para>
    /// <para>
    /// The whole subsystem is effectively a no-op when
    /// <c>AppConfig.AI.Identity.Enabled</c> is <c>false</c> because the
    /// <c>AgentIdentityResolutionBehavior</c> pipeline behavior short-circuits
    /// before calling the resolver. The providers and validator remain
    /// registered but never run.
    /// </para>
    /// </remarks>
    private static void RegisterIdentityServices(IServiceCollection services)
    {
        // Credential providers — registered in priority-order for readability;
        // EntraAgentIdResolver enforces the actual hierarchy and is registration-order
        // agnostic.
        services.AddSingleton<IAgentCredentialProvider, FederatedCredentialProvider>();
        services.AddSingleton<IAgentCredentialProvider, ManagedIdentityCredentialProvider>();
        services.AddSingleton<IAgentCredentialProvider, CertificateCredentialProvider>();
        services.AddSingleton<IAgentCredentialProvider, ClientSecretCredentialProvider>();
        services.AddSingleton<IAgentCredentialProvider, DevelopmentAgentCredentialProvider>();

        // Resolver — orchestrates the providers in fixed hierarchy order.
        services.AddSingleton<IAgentIdentityResolver, EntraAgentIdResolver>();

        // Tool-level RBAC validator — consults the static per-agent allowlist in
        // AppConfig.AI.Identity.ToolAuthorization.
        services.AddSingleton<IAgentIdentityValidator, EntraAgentIdentityValidator>();
    }
}
