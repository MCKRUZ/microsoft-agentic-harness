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
		services.AddTransient<App>();

		var serviceProvider = services.BuildServiceProvider();

		// Start hosted services (skill seeding, etc.) — required because
		// the console app doesn't use IHost which would start them automatically
		foreach (var hostedService in serviceProvider.GetServices<IHostedService>())
			await hostedService.StartAsync(CancellationToken.None);

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
}
