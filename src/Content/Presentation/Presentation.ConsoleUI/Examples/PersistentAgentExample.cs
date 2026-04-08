using Application.AI.Common.Interfaces;
using Domain.AI.Agents;
using Domain.Common.Config.AI;
using Microsoft.Extensions.Logging;
using Presentation.ConsoleUI.Common.Helpers;
using Spectre.Console;

namespace Presentation.ConsoleUI.Examples;

/// <summary>
/// Demonstrates Azure AI Foundry persistent agents: creating new server-side agents,
/// looking up existing agents by ID, and showing provider availability.
/// </summary>
public class PersistentAgentExample
{
    private readonly IAgentFactory _agentFactory;
    private readonly IChatClientFactory _chatClientFactory;
    private readonly ILogger<PersistentAgentExample> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PersistentAgentExample"/> class.
    /// </summary>
    /// <param name="agentFactory">Factory for creating and looking up agents.</param>
    /// <param name="chatClientFactory">Factory for creating persistent agents in AI Foundry.</param>
    /// <param name="logger">Logger instance.</param>
    public PersistentAgentExample(
        IAgentFactory agentFactory,
        IChatClientFactory chatClientFactory,
        ILogger<PersistentAgentExample> logger)
    {
        _agentFactory = agentFactory;
        _chatClientFactory = chatClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Runs the interactive persistent agent demo.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        ConsoleHelper.DisplayHeader("Persistent Agent (AI Foundry)", Color.Purple);

        DisplayProviderAvailability();

        while (true)
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]Select action:[/]")
                    .AddChoices("Create New Agent", "Lookup Existing Agent", "Back"));

            if (choice == "Back") return;

            if (choice == "Create New Agent")
                await CreateAgentAsync(cancellationToken);
            else
                await LookupAgentAsync(cancellationToken);
        }
    }

    private void DisplayProviderAvailability()
    {
        var providers = _agentFactory.GetAvailableProviders();

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("[bold]Provider[/]");
        table.AddColumn("[bold]Available[/]");

        foreach (var (clientType, available) in providers)
        {
            var statusColor = available ? "green" : "red";
            table.AddRow(
                Markup.Escape(clientType.ToString()),
                $"[{statusColor}]{available}[/]");
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private async Task CreateAgentAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!_chatClientFactory.IsAvailable(AIAgentFrameworkClientType.PersistentAgents))
            {
                ConsoleHelper.DisplayError("AI Foundry is not configured. Set AppConfig:AI:AIFoundry:ProjectEndpoint to use this feature.");
                return;
            }

            var name = AnsiConsole.Ask<string>("[bold]Agent name:[/]");
            var instructions = AnsiConsole.Ask<string>("[bold]Agent instructions:[/]");
            var model = AnsiConsole.Ask("[bold]Model deployment[/] [grey](default: gpt-4o)[/]:", "gpt-4o");

            string agentId = string.Empty;

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("purple"))
                .StartAsync("Creating persistent agent in AI Foundry...", async _ =>
                {
                    agentId = await _chatClientFactory.CreatePersistentAgentAsync(
                        model, name, instructions, cancellationToken: cancellationToken);
                });

            var resultTable = new Table().Border(TableBorder.Rounded);
            resultTable.AddColumn("[bold]Property[/]");
            resultTable.AddColumn("[bold]Value[/]");
            resultTable.AddRow("Agent Name", $"[cornflowerblue]{Markup.Escape(name)}[/]");
            resultTable.AddRow("Agent ID", $"[green]{Markup.Escape(agentId)}[/]");
            resultTable.AddRow("Model", Markup.Escape(model));
            resultTable.AddRow("Status", "[green]Created[/]");

            AnsiConsole.Write(resultTable);
            ConsoleHelper.DisplaySuccess($"Persistent agent created with ID: {agentId}");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "AI Foundry not configured");
            ConsoleHelper.DisplayError("Configure AppConfig:AI:AIFoundry to use this feature.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create persistent agent");
            ConsoleHelper.DisplayError($"Agent creation failed: {ex.Message}");
        }
    }

    private async Task LookupAgentAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!_chatClientFactory.IsAvailable(AIAgentFrameworkClientType.PersistentAgents))
            {
                ConsoleHelper.DisplayError("AI Foundry is not configured. Set AppConfig:AI:AIFoundry:ProjectEndpoint to use this feature.");
                return;
            }

            var agentId = AnsiConsole.Ask<string>("[bold]Enter agent ID:[/]");

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("purple"))
                .StartAsync("Looking up agent in AI Foundry...", async _ =>
                {
                    var context = new AgentExecutionContext
                    {
                        Name = "lookup-agent",
                        AgentId = agentId,
                        AIAgentFrameworkType = AIAgentFrameworkClientType.PersistentAgents
                    };

                    var agent = await _agentFactory.CreateAgentAsync(context, cancellationToken);

                    ConsoleHelper.DisplaySuccess($"Agent '{agentId}' found and initialized successfully.");
                });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "AI Foundry not configured");
            ConsoleHelper.DisplayError("Configure AppConfig:AI:AIFoundry to use this feature.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to lookup persistent agent");
            ConsoleHelper.DisplayError($"Agent lookup failed: {ex.Message}");
        }
    }
}
