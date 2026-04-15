diff --git a/src/Content/Presentation/Presentation.ConsoleUI/App.cs b/src/Content/Presentation/Presentation.ConsoleUI/App.cs
index 88617d7..1b68914 100644
--- a/src/Content/Presentation/Presentation.ConsoleUI/App.cs
+++ b/src/Content/Presentation/Presentation.ConsoleUI/App.cs
@@ -22,6 +22,7 @@ public class App
 	private readonly PersistentAgentExample _persistentAgentExample;
 	private readonly A2AExample _a2aExample;
 	private readonly SetupSecretsExample _setupSecretsExample;
+	private readonly OptimizeExample _optimizeExample;
 
 	public App(
 		IOptionsMonitor<AppConfig> appConfig,
@@ -32,7 +33,8 @@ public class App
 		ToolConverterExample toolConverterExample,
 		PersistentAgentExample persistentAgentExample,
 		A2AExample a2aExample,
-		SetupSecretsExample setupSecretsExample)
+		SetupSecretsExample setupSecretsExample,
+		OptimizeExample optimizeExample)
 	{
 		_appConfig = appConfig;
 		_loggerFactory = loggerFactory;
@@ -43,6 +45,7 @@ public class App
 		_persistentAgentExample = persistentAgentExample;
 		_a2aExample = a2aExample;
 		_setupSecretsExample = setupSecretsExample;
+		_optimizeExample = optimizeExample;
 	}
 
@@ -87,6 +90,9 @@ public class App
 			case "setup-secrets":
 				await _setupSecretsExample.RunAsync();
 				break;
+			case "optimize":
+				await _optimizeExample.RunAsync();
+				break;
 			default:
 				ConsoleHelper.DisplayError($"Unknown example: {exampleName}");
 				break;
@@ -103,7 +109,8 @@ public class App
 				.HighlightStyle(Style.Parse("cornflowerblue"))
 				.AddChoiceGroup("[bold]Agents[/]",
 					"Research Agent (Standalone)",
-					"Orchestrator Agent (Multi-Agent)")
+					"Orchestrator Agent (Multi-Agent)",
+					"Meta-Harness Optimizer")
 				.AddChoiceGroup("[bold]Advanced[/]",
 					"MCP Tools Discovery",
 					"Tool Converter Demo",
@@ -126,6 +133,10 @@ public class App
 					await _orchestratorExample.RunAsync();
 					break;
 
+				case "Meta-Harness Optimizer":
+					await _optimizeExample.RunAsync();
+					break;
+
 				case "MCP Tools Discovery":
 					await _mcpToolsExample.RunAsync();
 					break;
diff --git a/src/Content/Presentation/Presentation.ConsoleUI/Examples/OptimizeExample.cs b/src/Content/Presentation/Presentation.ConsoleUI/Examples/OptimizeExample.cs
new file mode 100644
index 0000000..08a4e3c
--- /dev/null
+++ b/src/Content/Presentation/Presentation.ConsoleUI/Examples/OptimizeExample.cs
@@ -0,0 +1,89 @@
+using Application.Core.CQRS.MetaHarness;
+using MediatR;
+using Microsoft.Extensions.Logging;
+using Presentation.ConsoleUI.Common.Helpers;
+using Spectre.Console;
+
+namespace Presentation.ConsoleUI.Examples;
+
+/// <summary>
+/// Interactive example that runs the meta-harness optimization loop.
+/// Prompts the user for an optional iteration override,
+/// dispatches <see cref="RunHarnessOptimizationCommand"/> via MediatR,
+/// and displays the final optimization result.
+/// </summary>
+public class OptimizeExample
+{
+	private readonly ISender _sender;
+	private readonly ILogger<OptimizeExample> _logger;
+
+	public OptimizeExample(ISender sender, ILogger<OptimizeExample> logger)
+	{
+		_sender = sender;
+		_logger = logger;
+	}
+
+	/// <summary>
+	/// Runs the interactive optimization session.
+	/// </summary>
+	public async Task RunAsync(CancellationToken cancellationToken = default)
+	{
+		ConsoleHelper.DisplayHeader("Meta-Harness Optimizer", Color.Gold1);
+
+		var maxIterationsRaw = AnsiConsole.Prompt(
+			new TextPrompt<string>("[bold]Max iterations override[/] [grey](leave empty to use config default):[/]")
+				.AllowEmpty());
+
+		int? maxIterations = null;
+		if (!string.IsNullOrWhiteSpace(maxIterationsRaw)
+		    && int.TryParse(maxIterationsRaw, out var parsed)
+		    && parsed > 0)
+		{
+			maxIterations = parsed;
+		}
+
+		var runId = Guid.NewGuid();
+		_logger.LogInformation("Starting optimization run {RunId}", runId);
+
+		OptimizationResult? result = null;
+
+		await AnsiConsole.Status()
+			.Spinner(Spinner.Known.Dots)
+			.SpinnerStyle(Style.Parse("gold1"))
+			.StartAsync("Running optimization loop...", async _ =>
+			{
+				result = await _sender.Send(new RunHarnessOptimizationCommand
+				{
+					OptimizationRunId = runId,
+					MaxIterations = maxIterations,
+				}, cancellationToken);
+			});
+
+		if (result is null) return;
+
+		if (result.IterationCount == 0)
+		{
+			ConsoleHelper.DisplayError(
+				"No iterations completed. Check that eval tasks exist at the configured EvalTasksPath.");
+			return;
+		}
+
+		AnsiConsole.MarkupLine(
+			$"\n[bold green]Optimization complete.[/] Ran {result.IterationCount} iteration(s). " +
+			$"Best score: [bold]{result.BestScore:P1}[/]");
+
+		if (!string.IsNullOrEmpty(result.ProposedChangesPath))
+		{
+			AnsiConsole.MarkupLine($"\nBest candidate written to:");
+			AnsiConsole.MarkupLine($"  [grey]{result.ProposedChangesPath}[/]");
+			AnsiConsole.MarkupLine(
+				"\n[grey]To review: inspect _proposed/ for modified skill files and system prompt.[/]");
+			AnsiConsole.MarkupLine(
+				"[grey]To promote: copy _proposed/ contents over the live skills/ directory.[/]");
+		}
+		else
+		{
+			AnsiConsole.MarkupLine("[grey]No proposed changes written (no successful candidates).[/]");
+		}
+	}
+}
diff --git a/src/Content/Presentation/Presentation.ConsoleUI/Program.cs b/src/Content/Presentation/Presentation.ConsoleUI/Program.cs
index c579bef..3a5f688 100644
--- a/src/Content/Presentation/Presentation.ConsoleUI/Program.cs
+++ b/src/Content/Presentation/Presentation.ConsoleUI/Program.cs
@@ -39,6 +39,7 @@ public class Program
 		services.AddTransient<PersistentAgentExample>();
 		services.AddTransient<A2AExample>();
 		services.AddTransient<SetupSecretsExample>();
+		services.AddTransient<OptimizeExample>();
 		services.AddTransient<App>();
 
 		var serviceProvider = services.BuildServiceProvider();
