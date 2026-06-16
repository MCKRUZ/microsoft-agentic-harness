using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Permissions;
using Application.Core.Escalation.Strategies;
using Application.Core.Permissions;
using Application.Core.Workflows.Governance;
using Application.Core.Workflows.KnowledgeGraph;
using Application.Core.Workflows.MetaHarness;
using Application.Core.Workflows.Rag;
using Domain.AI.Escalation;
using FluentValidation;
using MediatR;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace Application.Core;

/// <summary>
/// Dependency injection configuration for the Application.Core layer.
/// Registers CQRS handlers, validators, MAF workflow factories, and agent-specific services.
/// </summary>
/// <remarks>
/// Called from the Presentation composition root after Application.AI.Common:
/// <code>
/// services.AddApplicationCommonDependencies(appConfig);
/// services.AddApplicationAIDependencies();
/// services.AddApplicationCoreDependencies();
/// </code>
/// </remarks>
public static class DependencyInjection
{
	/// <summary>
	/// Registers all Application.Core dependencies into the service collection.
	/// </summary>
	public static IServiceCollection AddApplicationCoreDependencies(
		this IServiceCollection services)
	{
		var assembly = typeof(DependencyInjection).Assembly;

		// Auto-discover MediatR handlers in this assembly
		services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(assembly));

		// Auto-discover FluentValidation validators in this assembly
		services.AddValidatorsFromAssembly(assembly);

		// Autonomy tier rule provider — generates baseline permission rules from agent tier
		services.AddSingleton<IPermissionRuleProvider, AutonomyTierRuleProvider>();

		// Plugin-boundary rule provider — generates permission rules from plugin governance config
		services.AddSingleton<IPermissionRuleProvider, PluginPermissionRuleProvider>();

		// Graded autonomy decision evaluator (PR-4) — consulted by the ChangeProposal
		// gate resolver to decide whether the approval gate is included in a proposal's
		// frozen gate list. When AppConfig.AI.Permissions.GradedAutonomy.Enabled is false
		// the evaluator returns the PR-2 fallback unchanged.
		services.AddSingleton<IAutonomyDecisionEvaluator, AutonomyDecisionEvaluator>();

		// Approval strategies — keyed by ApprovalStrategyType for IEscalationService to resolve
		services.AddKeyedSingleton<IApprovalStrategy>(ApprovalStrategyType.AnyOf, (_, _) => new AnyOfApprovalStrategy());
		services.AddKeyedSingleton<IApprovalStrategy>(ApprovalStrategyType.AllOf, (_, _) => new AllOfApprovalStrategy());
		services.AddKeyedSingleton<IApprovalStrategy>(ApprovalStrategyType.Quorum, (_, _) => new QuorumApprovalStrategy());

		services.AddWorkflowDependencies();

		return services;
	}

	/// <summary>
	/// Registers MAF workflow instances as keyed services, built lazily from
	/// DI-resolved services. Each workflow is keyed by its logical name for
	/// selective resolution.
	/// </summary>
	/// <remarks>
	/// <para>Workflow keys (singleton unless noted):</para>
	/// <list type="bullet">
	///   <item><c>"rag-pipeline"</c> — RAG retrieval pipeline with CRAG evaluation</item>
	///   <item><c>"kg-ingestion"</c> — Knowledge graph entity extraction and storage</item>
	///   <item><c>"governance-approval"</c> — Human-in-the-loop approval with <see cref="RequestPort"/></item>
	///   <item>
	///     <c>"optimization-iteration"</c> — Single meta-harness propose-evaluate-score iteration.
	///     Registered <b>scoped</b> (not singleton) because its build resolves scoped services
	///     (<c>IHarnessProposer</c>, <c>IEvaluationService</c>). Resolve it from a created scope's
	///     provider, e.g.
	///     <c>scope.ServiceProvider.GetRequiredKeyedService&lt;Workflow&gt;("optimization-iteration")</c>,
	///     never from the root provider.
	///   </item>
	/// </list>
	/// <para>
	/// The multi-agent orchestration workflow (<see cref="Workflows.Orchestration.MultiAgentWorkflow"/>)
	/// is not registered here because it requires runtime configuration (agent list and chat client).
	/// Build it on-demand via <c>MultiAgentWorkflow.BuildAsync()</c>.
	/// </para>
	/// </remarks>
	private static IServiceCollection AddWorkflowDependencies(this IServiceCollection services)
	{
		services.AddKeyedSingleton<Workflow>("rag-pipeline",
			(sp, _) => RagPipelineWorkflow.Build(sp));

		services.AddKeyedSingleton<Workflow>("kg-ingestion",
			(sp, _) => KgIngestionWorkflow.Build(sp));

		// Governance: build once, register both Workflow and RequestPort
		services.AddSingleton(sp =>
		{
			var (workflow, port) = GovernanceApprovalWorkflow.Build(sp);
			return new GovernanceApprovalComponents(workflow, port);
		});

		services.AddKeyedSingleton<Workflow>("governance-approval",
			(sp, _) => sp.GetRequiredService<GovernanceApprovalComponents>().Workflow);

		services.AddKeyedSingleton<RequestPort>("governance-approval",
			(sp, _) => sp.GetRequiredService<GovernanceApprovalComponents>().ApprovalPort);

		// Registered as KEYED SCOPED, not singleton, because OptimizationIterationWorkflow.Build
		// resolves IHarnessProposer and IEvaluationService, both of which are registered SCOPED in
		// Infrastructure.AI. A keyed-singleton factory receives the root provider; resolving scoped
		// services from it throws under scope validation and captures them for the process lifetime
		// otherwise. A scoped registration hands Build the consumer's scoped provider so the scoped
		// dependencies resolve correctly. The other three workflow keys remain singletons because
		// their dependencies are all singletons. The workflow is cheap to rebuild per scope (it only
		// wires stateless executors).
		services.AddKeyedScoped<Workflow>("optimization-iteration",
			(sp, _) => OptimizationIterationWorkflow.Build(sp));

		return services;
	}

	/// <summary>
	/// Holds both the governance workflow and its <see cref="RequestPort"/> so they are
	/// built once from <see cref="GovernanceApprovalWorkflow.Build"/> and both are accessible.
	/// </summary>
	private sealed record GovernanceApprovalComponents(Workflow Workflow, RequestPort ApprovalPort);
}
