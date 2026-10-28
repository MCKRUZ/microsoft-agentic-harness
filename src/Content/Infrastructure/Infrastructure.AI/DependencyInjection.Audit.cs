using Application.AI.Common.Interfaces.Audit;
using Application.AI.Common.Interfaces.Changes;
using Application.AI.Common.Interfaces.DriftDetection;
using Application.AI.Common.Interfaces.Egress;
using Application.AI.Common.Interfaces.Escalation;
using Domain.Common.Config;
using Infrastructure.AI.Audit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI;

public static partial class DependencyInjection
{
    /// <summary>
    /// Registers audit-chain verification: forwards each of the five hash-chained audit writers
    /// owned by this project as an <see cref="IVerifiableAuditChain"/> (pointing at the existing
    /// singleton, not a new instance) and, when enabled, the scheduled
    /// <see cref="AuditChainVerificationService"/>.
    /// </summary>
    /// <remarks>
    /// The forwarding registrations must run after the change, egress, escalation, drift and
    /// compliance audit writers are registered. A sixth chain, the governance audit writer, is registered
    /// separately by <c>Infrastructure.AI.Governance/DependencyInjection.cs</c> (#407) — this method
    /// does not need to know about it: <see cref="AuditChainVerificationService"/> resolves every
    /// <see cref="IVerifiableAuditChain"/> registration via <c>IEnumerable&lt;&gt;</c>, from whichever
    /// project registered it. The hosted service is gated on
    /// <c>AppConfig.AI.Audit.VerificationEnabled</c> (on by default) and resolves
    /// <see cref="TimeProvider"/> with a system fallback so it works whether or not the host
    /// registered one.
    /// </remarks>
    private static void RegisterAuditChainVerification(IServiceCollection services, AppConfig appConfig)
    {
        // The keyed references and fingerprints the trails record in place of people and tool arguments (#903).
        // Resolved at startup so an unusable key, or a key file that cannot be written, stops the host at boot.
        services.TryAddSingleton<IAuditPseudonymizer, HmacAuditPseudonymizer>();
        services.AddHostedService<AuditPseudonymKeyStartup>();

        services.AddSingleton<IVerifiableAuditChain>(sp =>
            (IVerifiableAuditChain)sp.GetRequiredService<IChangeAuditWriter>());
        services.AddSingleton<IVerifiableAuditChain>(sp =>
            (IVerifiableAuditChain)sp.GetRequiredService<IEgressAuditWriter>());
        services.AddSingleton<IVerifiableAuditChain>(sp =>
            (IVerifiableAuditChain)sp.GetRequiredService<IEscalationAuditStore>());
        services.AddSingleton<IVerifiableAuditChain>(sp =>
            (IVerifiableAuditChain)sp.GetRequiredService<IDriftAuditStore>());
        services.AddSingleton<IVerifiableAuditChain>(sp =>
            (IVerifiableAuditChain)sp.GetRequiredService<Application.Core.Compliance.IComplianceAuditTrail>());

        // Registered whether or not scheduled verification runs: the compliance report reads checkpoints
        // too, and with verification off there are simply none to read.
        services.AddSingleton<IAuditChainCheckpoints, AuditChainCheckpointStore>();

        if (!appConfig.AI.Audit.VerificationEnabled)
            return;

        services.AddSingleton(sp => new AuditChainVerificationService(
            sp.GetServices<IVerifiableAuditChain>(),
            sp.GetRequiredService<IAuditChainCheckpoints>(),
            sp.GetRequiredService<IOptionsMonitor<AppConfig>>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetRequiredService<ILogger<AuditChainVerificationService>>()));
        services.AddHostedService(sp => sp.GetRequiredService<AuditChainVerificationService>());
    }
}
