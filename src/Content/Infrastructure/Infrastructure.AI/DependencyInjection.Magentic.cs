using Application.AI.Common.Interfaces.Orchestration.Magentic;
using Infrastructure.AI.Orchestration.Magentic;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.AI;

public static partial class DependencyInjection
{
    /// <summary>
    /// Registers the Magentic orchestration subsystem (PR-6): single
    /// <see cref="MagenticSpanEmitter"/> per process, the
    /// <see cref="MagenticChangeProposalRouter"/> for state-change replan
    /// routing, the production HITL bridge wired through
    /// <c>IEscalationService</c>, and the <see cref="IMagenticOrchestrator"/>
    /// public surface.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All registrations are unconditional — <see cref="IMagenticOrchestrator"/>
    /// is inert until a consumer calls <see cref="IMagenticOrchestrator.RunAsync"/>
    /// with a request. There is no <c>AppConfig.AI.Magentic.Enabled</c> flag
    /// because the orchestrator is the only entry point; an absent caller
    /// equals an absent feature.
    /// </para>
    /// <para>
    /// The <see cref="MagenticSpanEmitter"/> is registered Singleton so a
    /// single <c>ActivitySource</c> serves all concurrent workflows in-process.
    /// The Presentation layer must add the source name
    /// (<c>AgenticHarness.Orchestration.Magentic</c> — see
    /// <c>Domain.AI.Telemetry.Conventions.MagenticConventions.ActivitySourceName</c>)
    /// to the OTel tracer provider for spans to surface.
    /// </para>
    /// </remarks>
    private static void RegisterMagenticServices(IServiceCollection services)
    {
        services.AddSingleton<MagenticSpanEmitter>();
        services.AddSingleton<MagenticChangeProposalRouter>();
        services.AddSingleton<IMagenticPlanReviewBridge, MagenticHitlBridge>();
        services.AddSingleton<IMagenticOrchestrator, MagenticOrchestrator>();
    }
}
