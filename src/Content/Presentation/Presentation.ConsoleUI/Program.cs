using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Presentation.Common.Extensions;
using Presentation.ConsoleUI.Examples;

namespace Presentation.ConsoleUI;

/// <summary>
/// Entry point for the Agentic Harness Console UI.
/// </summary>
/// <remarks>
/// <para><b>Command-line arguments:</b></para>
/// <list type="bullet">
///   <item><c>--example research</c> — Run the research agent demo non-interactively</item>
///   <item><c>--example orchestrator</c> — Run the orchestrator demo non-interactively</item>
///   <item><c>--example mcp-tools</c> — Run the MCP tools discovery demo non-interactively</item>
///   <item><c>--example tool-converter</c> — Run the tool converter demo non-interactively</item>
///   <item><c>--example persistent-agent</c> — Run the persistent agent demo non-interactively</item>
///   <item><c>--example a2a</c> — Run the A2A agent-to-agent demo non-interactively</item>
///   <item><c>--example a2a-sre-workspace</c> — Run the A2A SRE-to-workspace demo non-interactively</item>
///   <item><c>--example magentic</c> — Run the Magentic orchestration demo non-interactively</item>
///   <item><c>--example skill-training</c> — Run the skill-training (SkillOpt) demo non-interactively</item>
///   <item><c>--example setup-secrets</c> — Run the user secrets setup wizard</item>
///   <item><c>--example optimize</c> — Run the meta-harness optimization loop</item>
///   <item>(no args) — Interactive menu mode</item>
/// </list>
/// </remarks>
public class Program
{
	public static async Task Main(string[] args)
	{
		var services = new ServiceCollection();

		// Register all layers: Domain, Application, Infrastructure, Presentation
		// HealthChecks UI disabled — requires a web server (IServer)
		services.GetServices(includeHealthChecksUI: false);

		// Register example classes
		services.AddTransient<ResearchAgentExample>();
		services.AddTransient<OrchestratorExample>();
		services.AddTransient<McpToolsExample>();
		services.AddTransient<ToolConverterExample>();
		services.AddTransient<PersistentAgentExample>();
		services.AddTransient<A2AExample>();
		services.AddTransient<SetupSecretsExample>();
		services.AddTransient<OptimizeExample>();
		services.AddTransient<RagPipelineExample>();
		services.AddTransient<KnowledgeGraphMemoryExample>();
		services.AddTransient<KnowledgeGraphComplianceExample>();
		services.AddTransient<GovernanceSanitizationExample>();
		services.AddTransient<EscalationApprovalsExample>();
		services.AddTransient<SkillsDiscoveryExample>();
		services.AddTransient<DriftDetectionExample>();
		services.AddTransient<LearningsLogExample>();
		services.AddTransient<ObservabilityBudgetExample>();
		services.AddTransient<MultiSourceRetrievalExample>();
		services.AddTransient<SandboxCapabilitiesExample>();
		services.AddTransient<PipelineBehaviorsExample>();
		services.AddTransient<SkillTrainingExample>();
		services.AddTransient<MagenticOrchestrationExample>();
		services.AddTransient<A2ASreToWorkspaceExample>();
		services.AddTransient<App>();

		// `await using` guarantees the container (and every IDisposable/IAsyncDisposable
		// singleton it owns — named-pipe logger provider, JSONL audit writers, EF Core
		// resources) is flushed and disposed on exit, mirroring Presentation.EvalRunner.
		await using var serviceProvider = services.BuildServiceProvider();

		// Track only the services we actually started so a mid-startup failure on service N
		// doesn't leave us calling StopAsync on services that never started.
		var allHostedServices = serviceProvider.GetServices<IHostedService>().ToList();
		var startedHostedServices = new List<IHostedService>(allHostedServices.Count);

		// Bound the shutdown wait so a hung hosted service can't pin the process forever.
		var shutdownTimeout = TimeSpan.FromSeconds(15);

		try
		{
			// Start hosted services (skill seeding, etc.) — required because the console app
			// doesn't use IHost which would start (and later stop) them automatically.
			foreach (var hostedService in allHostedServices)
			{
				await hostedService.StartAsync(CancellationToken.None);
				startedHostedServices.Add(hostedService);
			}

			var app = serviceProvider.GetRequiredService<App>();

			// Route based on command-line arguments
			if (args.Length >= 2 && args[0].Equals("--example", StringComparison.OrdinalIgnoreCase))
			{
				await app.RunExampleAsync(args[1]);
			}
			else
			{
				await app.RunAsync();
			}
		}
		finally
		{
			// Gracefully stop background services so in-flight work drains and buffered state
			// (retry-queue requests, audit writes) is flushed before the provider is disposed.
			using var stopCts = new CancellationTokenSource(shutdownTimeout);
			foreach (var hostedService in startedHostedServices)
			{
				try { await hostedService.StopAsync(stopCts.Token); }
				catch { /* best-effort; we're shutting down */ }
			}
		}
	}
}
