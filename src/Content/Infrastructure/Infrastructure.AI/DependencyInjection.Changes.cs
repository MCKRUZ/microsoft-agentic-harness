using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using Infrastructure.AI.Changes;
using Infrastructure.AI.Changes.Gates;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.AI;

public static partial class DependencyInjection
{
    /// <summary>
    /// Registers the ChangeProposal pipeline subsystem (PR-2): orchestrator,
    /// audit writer, evidence store, in-memory proposal store, default gate
    /// resolver + approval router, the four built-in gates keyed by
    /// <c>WellKnownGateKeys</c>, and fail-loud <c>NotConfigured*</c> defaults
    /// per <see cref="ChangeTargetKind"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All registrations are unconditional — the orchestrator and CQRS surface
    /// short-circuit when <c>AppConfig.AI.Changes.Enabled</c> is false, so the
    /// graph stays inert until a consumer opts in. Real validators and policies
    /// are NOT registered here; consumers wire those after this call (or wait
    /// for the per-target-kind skill packs in PR-8 / PR-9 / PR-10).
    /// </para>
    /// <para>
    /// The <c>NotConfiguredValidator</c> is registered for every
    /// <see cref="ChangeTargetKind"/> so a consumer who enables the pipeline
    /// without wiring real validators gets a fail-fast exception with a
    /// directive message on the very first proposal — never silently passing
    /// every validation gate.
    /// </para>
    /// <para>
    /// Gates registered keyed by <c>WellKnownGateKeys.*</c> so the orchestrator
    /// resolves them via <c>IServiceProvider.GetKeyedService&lt;IChangeProposalGate&gt;</c>.
    /// Consumers can replace any gate by re-registering with the same key
    /// after this call.
    /// </para>
    /// </remarks>
    private static void RegisterChangesServices(IServiceCollection services)
    {
        // --- Store + audit + evidence ---
        services.AddSingleton<IChangeProposalStore, InMemoryChangeProposalStore>();
        services.AddSingleton<IChangeAuditWriter, JsonlChangeAuditWriter>();
        services.AddSingleton<IEvidenceStore, FileSystemEvidenceStore>();

        // --- Default resolver + approval router ---
        services.AddSingleton<IChangeProposalGateResolver, DefaultChangeProposalGateResolver>();
        services.AddSingleton<IChangeApprovalRouter, EscalationServiceApprovalRouter>();

        // --- The four built-in gates, keyed by WellKnownGateKeys ---
        services.AddKeyedSingleton<IChangeProposalGate>(
            WellKnownGateKeys.SelfValidation,
            (sp, _) => new SelfValidationGate(sp, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SelfValidationGate>>()));
        services.AddKeyedSingleton<IChangeProposalGate>(
            WellKnownGateKeys.Policy,
            (sp, _) => new PolicyGate(
                sp.GetServices<IChangeProposalPolicy>(),
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Domain.Common.Config.AppConfig>>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PolicyGate>>()));
        services.AddKeyedSingleton<IChangeProposalGate>(
            WellKnownGateKeys.Approval,
            (sp, _) => new ApprovalGate(
                sp.GetRequiredService<IChangeApprovalRouter>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ApprovalGate>>()));
        services.AddKeyedSingleton<IChangeProposalGate>(
            WellKnownGateKeys.Merge,
            (sp, _) => new MergeGate(sp, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MergeGate>>()));

        // --- Fail-loud NotConfigured defaults per target kind ---
        // Registered under each ChangeTargetKind so the SelfValidationGate's
        // GetKeyedServices enumeration finds at least one validator and throws
        // a directive exception if real validators haven't been wired. Consumers
        // who add real validators under the same key will be enumerated alongside
        // the placeholder; the placeholder throws on first use so the misconfig
        // surfaces immediately rather than producing a misleading silent pass.
        // NOTE: when a real validator is registered under the same key, the
        // placeholder is still present — consumers who want it removed entirely
        // should call services.RemoveAll<...>() before AddInfrastructureAIDependencies
        // or substitute the SelfValidationGate.
        services.AddKeyedSingleton<IChangeProposalValidator>(
            ChangeTargetKind.GitRepo,
            (_, _) => new NotConfiguredValidator(ChangeTargetKind.GitRepo));
        services.AddKeyedSingleton<IChangeProposalValidator>(
            ChangeTargetKind.KubernetesResource,
            (_, _) => new NotConfiguredValidator(ChangeTargetKind.KubernetesResource));
        services.AddKeyedSingleton<IChangeProposalValidator>(
            ChangeTargetKind.IacDeployment,
            (_, _) => new NotConfiguredValidator(ChangeTargetKind.IacDeployment));

        // No NotConfiguredPolicy registration — the PolicyGate's "no policies
        // registered" branch already produces a directive Fail. Registering the
        // placeholder would short-circuit that branch and throw an exception
        // instead, which loses the orchestrator's safe Fail→Rejected handling.

        // --- The orchestrator itself ---
        services.AddSingleton<IChangeProposalOrchestrator, ChangeProposalOrchestrator>();

        // --- Startup-time validator ---
        // Refuses to boot when Changes.Enabled = true and the registered store
        // is in-memory in a non-Development environment, or when the default
        // approval router has no configured approvers. See
        // ChangeProposalStartupValidator for the full rule set.
        services.AddHostedService<ChangeProposalStartupValidator>();
    }
}
