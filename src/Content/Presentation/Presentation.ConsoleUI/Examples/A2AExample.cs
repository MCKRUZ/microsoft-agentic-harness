using Application.AI.Common.Interfaces.A2A;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Presentation.ConsoleUI.Common.Helpers;
using Spectre.Console;

namespace Presentation.ConsoleUI.Examples;

/// <summary>
/// Demonstrates the Agent-to-Agent (A2A) protocol: displays the local agent card,
/// lists configured remote agents, discovers remote capabilities, and sends
/// tasks to remote agents.
/// </summary>
public class A2AExample
{
    private readonly IA2AAgentHost _a2aHost;
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly ILogger<A2AExample> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="A2AExample"/> class.
    /// </summary>
    /// <param name="a2aHost">A2A agent host for discovery and task delegation.</param>
    /// <param name="appConfig">Application configuration for A2A settings.</param>
    /// <param name="logger">Logger instance.</param>
    public A2AExample(
        IA2AAgentHost a2aHost,
        IOptionsMonitor<AppConfig> appConfig,
        ILogger<A2AExample> logger)
    {
        _a2aHost = a2aHost;
        _appConfig = appConfig;
        _logger = logger;
    }

    /// <summary>
    /// Runs the interactive A2A agent-to-agent demo.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        ConsoleHelper.DisplayHeader("A2A Agent-to-Agent", Color.Green);

        DisplayLocalAgentCard();
        DisplayRemoteAgents();

        while (true)
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]Select action:[/]")
                    .AddChoices("Discover Remote Agents", "Send Task to Agent", "Back"));

            if (choice == "Back") return;

            if (choice == "Discover Remote Agents")
                await DiscoverAgentsAsync(cancellationToken);
            else
                await SendTaskAsync(cancellationToken);
        }
    }

    private void DisplayLocalAgentCard()
    {
        try
        {
            var card = _a2aHost.GetAgentCard();

            var table = new Table().Border(TableBorder.Rounded);
            table.Title("[bold]Local Agent Card[/]");
            table.AddColumn("[bold]Property[/]");
            table.AddColumn("[bold]Value[/]");

            table.AddRow("Name", $"[cornflowerblue]{Markup.Escape(card.Name)}[/]");
            table.AddRow("Description", Markup.Escape(card.Description));
            table.AddRow("URL", Markup.Escape(card.Url ?? "(not set)"));
            table.AddRow("Version", Markup.Escape(card.Version ?? "(not set)"));
            table.AddRow("Capabilities", card.Capabilities.Count > 0
                ? string.Join(", ", card.Capabilities.Select(c => $"[cyan]{Markup.Escape(c)}[/]"))
                : "[grey]none[/]");
            table.AddRow("Skills", card.Skills.Count > 0
                ? string.Join(", ", card.Skills.Select(s => $"[yellow]{Markup.Escape(s)}[/]"))
                : "[grey]none[/]");

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get local agent card");
            ConsoleHelper.DisplayError($"Could not load local agent card: {ex.Message}");
        }
    }

    private void DisplayRemoteAgents()
    {
        var a2aConfig = _appConfig.CurrentValue.AI?.A2A;
        var remotes = a2aConfig?.RemoteAgents;

        if (remotes is null || remotes.Count == 0)
        {
            ConsoleHelper.DisplayInfo("Remote Agents", "No remote agents configured. Add entries to AppConfig:AI:A2A:RemoteAgents.");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.Title("[bold]Configured Remote Agents[/]");
        table.AddColumn("[bold]Name[/]");
        table.AddColumn("[bold]URL[/]");

        foreach (var remote in remotes)
        {
            table.AddRow(
                Markup.Escape(remote.Name),
                Markup.Escape(remote.Url));
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private async Task DiscoverAgentsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var a2aConfig = _appConfig.CurrentValue.AI?.A2A;
            if (a2aConfig is null || !a2aConfig.HasRemoteAgents)
            {
                ConsoleHelper.DisplayError("No remote agents configured for discovery.");
                return;
            }

            IReadOnlyList<Domain.AI.A2A.AgentCard> discovered;

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("green"))
                .StartAsync("Discovering remote agents...", async _ =>
                {
                    discovered = await _a2aHost.DiscoverAgentsAsync(cancellationToken);

                    if (discovered.Count == 0)
                    {
                        ConsoleHelper.DisplayInfo("Discovery", "No remote agents responded. They may not be running.");
                        return;
                    }

                    var tree = new Tree("[bold green]Discovered Agents[/]");

                    foreach (var card in discovered)
                    {
                        var agentNode = tree.AddNode($"[cornflowerblue]{Markup.Escape(card.Name)}[/]");
                        agentNode.AddNode($"[grey]URL:[/] {Markup.Escape(card.Url ?? "unknown")}");
                        agentNode.AddNode($"[grey]Description:[/] {Markup.Escape(card.Description)}");

                        if (card.Capabilities.Count > 0)
                            agentNode.AddNode($"[grey]Capabilities:[/] {string.Join(", ", card.Capabilities)}");

                        if (card.Skills.Count > 0)
                            agentNode.AddNode($"[grey]Skills:[/] {string.Join(", ", card.Skills)}");
                    }

                    AnsiConsole.Write(tree);
                    AnsiConsole.WriteLine();

                    ConsoleHelper.DisplaySuccess($"Discovered {discovered.Count} remote agents.");
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to discover remote agents");
            ConsoleHelper.DisplayError($"Agent discovery failed: {ex.Message}");
        }
    }

    private async Task SendTaskAsync(CancellationToken cancellationToken)
    {
        try
        {
            var a2aConfig = _appConfig.CurrentValue.AI?.A2A;
            var remotes = a2aConfig?.RemoteAgents;

            if (remotes is null || remotes.Count == 0)
            {
                ConsoleHelper.DisplayError("No remote agents configured. Add entries to AppConfig:AI:A2A:RemoteAgents.");
                return;
            }

            var agentName = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]Select remote agent:[/]")
                    .AddChoices(remotes.Select(r => r.Name)));

            var selectedAgent = remotes.First(r => r.Name == agentName);
            var taskDescription = AnsiConsole.Ask<string>("[bold]Task description:[/]");

            if (string.IsNullOrWhiteSpace(taskDescription)) return;

            string response = string.Empty;

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("green"))
                .StartAsync($"Sending task to {agentName}...", async _ =>
                {
                    response = await _a2aHost.SendTaskAsync(selectedAgent.Url, taskDescription, cancellationToken);
                });

            ConsoleHelper.DisplayInfo($"Response from {agentName}", Markup.Escape(response));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send task to remote agent");
            ConsoleHelper.DisplayError($"Task delegation failed: {ex.Message}");
        }
    }
}
