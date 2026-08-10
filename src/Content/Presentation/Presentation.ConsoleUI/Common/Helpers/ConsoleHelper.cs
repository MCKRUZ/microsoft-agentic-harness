using System.Text.RegularExpressions;
using Application.AI.Common.Interfaces;
using Domain.AI.Agents;
using Spectre.Console;

namespace Presentation.ConsoleUI.Common.Helpers;

/// <summary>
/// Reusable Spectre.Console UI rendering utilities.
/// </summary>
public static partial class ConsoleHelper
{
	private static readonly Regex MarkupTagsRegex = StripMarkupRegex();

	public static void DisplayHeader(string title, Color? color = null)
	{
		AnsiConsole.Write(new FigletText(title).Color(color ?? Color.Blue));
		AnsiConsole.WriteLine();
	}

	public static void DisplayError(string message)
	{
		var panel = new Panel(new Markup($"[red]{Markup.Escape(message)}[/]"))
		{
			Header = new PanelHeader("[red]Error[/]"),
			Border = BoxBorder.Rounded
		};
		AnsiConsole.Write(panel);
	}

	public static void DisplaySuccess(string message)
	{
		AnsiConsole.MarkupLine($"[green]{Markup.Escape(message)}[/]");
	}

	public static void DisplayInfo(string title, string content)
	{
		var panel = new Panel(new Markup(content))
		{
			Header = new PanelHeader($"[cornflowerblue]{Markup.Escape(title)}[/]"),
			Border = BoxBorder.Rounded
		};
		AnsiConsole.Write(panel);
	}

	/// <summary>
	/// Looks up <paramref name="agentId"/> in the registry, displaying the standard "not found"
	/// error and returning <c>null</c> if it isn't registered. Centralizes the lookup-or-error
	/// pattern shared by every console demo that opens on a specific built-in agent.
	/// </summary>
	public static AgentDefinition? RequireAgent(IAgentMetadataRegistry agentRegistry, string agentId)
	{
		var agentDef = agentRegistry.TryGet(agentId);
		if (agentDef is null)
			DisplayError($"Agent '{agentId}' was not found. Check AGENT.md search paths in AppConfig.AI.Agents.");

		return agentDef;
	}

	public static void DisplayAgentInfo(string name, string description, string agentType, IReadOnlyList<string>? tools = null)
	{
		var table = new Table().Border(TableBorder.Rounded);
		table.AddColumn("[bold]Property[/]");
		table.AddColumn("[bold]Value[/]");

		table.AddRow("Name", $"[cornflowerblue]{Markup.Escape(name)}[/]");
		table.AddRow("Description", Markup.Escape(description));
		table.AddRow("Type", $"[yellow]{Markup.Escape(agentType)}[/]");

		if (tools?.Count > 0)
			table.AddRow("Tools", string.Join(", ", tools.Select(t => $"[cyan]{Markup.Escape(t)}[/]")));

		AnsiConsole.Write(table);
		AnsiConsole.WriteLine();
	}

	public static void DisplayTurnResult(int turnNumber, string agentName, string response, IReadOnlyList<string> toolsInvoked)
	{
		var rule = new Rule($"[bold]Turn {turnNumber} - {Markup.Escape(agentName)}[/]");
		rule.RuleStyle("grey");
		AnsiConsole.Write(rule);

		if (toolsInvoked.Count > 0)
			AnsiConsole.MarkupLine($"[grey]Tools: {string.Join(", ", toolsInvoked.Select(t => $"[cyan]{Markup.Escape(t)}[/]"))}[/]");

		AnsiConsole.WriteLine(response);
		AnsiConsole.WriteLine();
	}

	public static void DisplayOrchestrationResult(string phase, string agent, string status, string? detail = null)
	{
		var statusColor = status.Contains("Complete", StringComparison.OrdinalIgnoreCase) ? "green"
			: status.Contains("Fail", StringComparison.OrdinalIgnoreCase) ? "red"
			: "yellow";

		AnsiConsole.MarkupLine($"  [{statusColor}]{Markup.Escape(status)}[/] [grey]|[/] [cornflowerblue]{Markup.Escape(agent)}[/] [grey]({Markup.Escape(phase)})[/]");

		if (!string.IsNullOrEmpty(detail))
			AnsiConsole.MarkupLine($"    [grey]{Markup.Escape(detail)}[/]");
	}

	/// <summary>
	/// Displays a numbered step indicator with description.
	/// </summary>
	public static void DisplayStep(int current, int total, string description)
	{
		AnsiConsole.MarkupLine($"\n[bold cornflowerblue][[Step {current}/{total}]][/] {Markup.Escape(description)}");
	}

	/// <summary>
	/// Displays a mode badge indicating live or offline operation.
	/// </summary>
	public static void DisplayModeInfo(bool isLive, string? detail = null)
	{
		if (isLive)
		{
			var msg = detail is not null ? $"[bold green][[LIVE]][/] {Markup.Escape(detail)}" : "[bold green][[LIVE]][/] Connected to configured backends";
			AnsiConsole.MarkupLine(msg);
		}
		else
		{
			var msg = detail is not null ? $"[bold yellow][[OFFLINE]][/] {Markup.Escape(detail)}" : "[bold yellow][[OFFLINE]][/] Using in-memory backends";
			AnsiConsole.MarkupLine(msg);
		}
	}

	/// <summary>
	/// Strips Spectre.Console markup tags from text for logging.
	/// </summary>
	public static string StripMarkup(string text) => MarkupTagsRegex.Replace(text, "");

	[GeneratedRegex(@"\[/?[^\]]+\]", RegexOptions.Compiled)]
	private static partial Regex StripMarkupRegex();
}
