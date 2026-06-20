using Application.AI.Common.Interfaces.Audit;
using Application.AI.Common.Interfaces.Changes;
using Application.AI.Common.Interfaces.DriftDetection;
using Application.AI.Common.Interfaces.Egress;
using Application.AI.Common.Interfaces.Escalation;
using Domain.Common.Config;
using Infrastructure.AI.Audit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI;

public static partial class DependencyInjection
{
    /// <summary>
    /// Registers audit-chain verification: forwards each of the four hash-chained audit writers
    /// as an <see cref="IVerifiableAuditChain"/> (pointing at the existing singleton, not a new
    /// instance) and, when enabled, the scheduled <see cref="AuditChainVerificationService"/>.
    /// </summary>
    /// <remarks>
    /// The forwarding registrations must run after the change, egress, escalation, and drift
    /// audit writers are registered. The hosted service is gated on
    /// <c>AppConfig.AI.Audit.VerificationEnabled</c> (on by default) and resolves
    /// <see cref="TimeProvider"/> with a system fallback so it works whether or not the host
    /// registered one.
    /// </remarks>
    private static void RegisterAuditChainVerification(IServiceCollection services, AppConfig appConfig)
    {
        services.AddSingleton<IVerifiableAuditChain>(sp =>
            (IVerifiableAuditChain)sp.GetRequiredService<IChangeAuditWriter>());
        services.AddSingleton<IVerifiableAuditChain>(sp =>
            (IVerifiableAuditChain)sp.GetRequiredService<IEgressAuditWriter>());
        services.AddSingleton<IVerifiableAuditChain>(sp =>
            (IVerifiableAuditChain)sp.GetRequiredService<IEscalationAuditStore>());
        services.AddSingleton<IVerifiableAuditChain>(sp =>
            (IVerifiableAuditChain)sp.GetRequiredService<IDriftAuditStore>());

        if (!appConfig.AI.Audit.VerificationEnabled)
            return;

        services.AddSingleton(sp => new AuditChainVerificationService(
            sp.GetServices<IVerifiableAuditChain>(),
            sp.GetRequiredService<IOptionsMonitor<AppConfig>>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetRequiredService<ILogger<AuditChainVerificationService>>()));
        services.AddHostedService(sp => sp.GetRequiredService<AuditChainVerificationService>());
    }
}
