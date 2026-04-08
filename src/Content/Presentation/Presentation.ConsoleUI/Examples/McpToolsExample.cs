using Application.AI.Common.Interfaces;
using Domain.Common.Config;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Presentation.ConsoleUI.Common.Helpers;
using Spectre.Console;

namespace Presentation.ConsoleUI.Examples;

/// <summary>
/// Demonstrates MCP (Model Context Protocol) tool discovery: lists configured
/// MCP servers, discovers tools from all active servers, and tests individual
/// server connectivity.
/// </summary>
public class McpToolsExample
{
    private readonly IMcpToolProvider _mcpToolProvider;
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly ILogger<McpToolsExample> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="McpToolsExample"/> class.
    /// </summary>
    /// <param name="mcpToolProvider">MCP tool provider for server discovery and tool listing.</param>
    /// <param name="appConfig">Application configuration for MCP server definitions.</param>
    /// <param name="logger">Logger instance.</param>
    public McpToolsExample(
        IMcpToolProvider mcpToolProvider,
        IOptionsMonitor<AppConfig> appConfig,
        ILogger<McpToolsExample> logger)
    {
        _mcpToolProvider = mcpToolProvider;
        _appConfig = appConfig;
        _logger = logger;
    }

    /// <summary>
    /// Runs the interactive MCP tools discovery demo.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        ConsoleHelper.DisplayHeader("MCP Tools Discovery", Color.Teal);

        var servers = _appConfig.CurrentValue.AI?.McpServers?.Servers;
        if (servers is null || servers.Count == 0)
        {
            ConsoleHelper.DisplayInfo("MCP Servers", "No MCP servers configured. Add servers to AppConfig:AI:McpServers:Servers.");
            return;
        }

        DisplayServerTable(servers);

        while (true)
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]Select action:[/]")
                    .AddChoices("Discover All Tools", "Test Server Connection", "Back"));

            if (choice == "Back") return;

            if (choice == "Discover All Tools")
                await DiscoverAllToolsAsync(cancellationToken);
            else
                await TestServerConnectionAsync(servers, cancellationToken);
        }
    }

    private static void DisplayServerTable(Dictionary<string, Domain.Common.Config.AI.MCP.McpServerDefinition> servers)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("[bold]Name[/]");
        table.AddColumn("[bold]Type[/]");
        table.AddColumn("[bold]Enabled[/]");

        foreach (var (name, def) in servers)
        {
            var enabledColor = def.Enabled ? "green" : "red";
            table.AddRow(
                Markup.Escape(name),
                Markup.Escape(def.Type.ToString()),
                $"[{enabledColor}]{def.Enabled}[/]");
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private async Task DiscoverAllToolsAsync(CancellationToken cancellationToken)
    {
        try
        {
            Dictionary<string, IList<AITool>> allTools;

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("teal"))
                .StartAsync("Discovering tools from all MCP servers...", async _ =>
                {
                    allTools = await _mcpToolProvider.GetAllToolsAsync(cancellationToken);

                    if (allTools.Count == 0)
                    {
                        ConsoleHelper.DisplayInfo("Discovery", "No tools discovered. MCP servers may not be running.");
                        return;
                    }

                    var tree = new Tree("[bold teal]MCP Tools[/]");

                    foreach (var (serverName, tools) in allTools)
                    {
                        var serverNode = tree.AddNode($"[cornflowerblue]{Markup.Escape(serverName)}[/] [grey]({tools.Count} tools)[/]");
                        foreach (var tool in tools)
                        {
                            var toolName = tool is AIFunction fn ? fn.Name : tool.ToString() ?? "unknown";
                            serverNode.AddNode($"[cyan]{Markup.Escape(toolName)}[/]");
                        }
                    }

                    AnsiConsole.Write(tree);
                    AnsiConsole.WriteLine();

                    var totalTools = allTools.Values.Sum(t => t.Count);
                    ConsoleHelper.DisplaySuccess($"Discovered {totalTools} tools across {allTools.Count} servers.");
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to discover MCP tools");
            ConsoleHelper.DisplayError($"Tool discovery failed: {ex.Message}");
        }
    }

    private async Task TestServerConnectionAsync(
        Dictionary<string, Domain.Common.Config.AI.MCP.McpServerDefinition> servers,
        CancellationToken cancellationToken)
    {
        var serverName = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Select server to test:[/]")
                .AddChoices(servers.Keys));

        try
        {
            var available = await _mcpToolProvider.IsServerAvailableAsync(serverName, cancellationToken);

            if (available)
                ConsoleHelper.DisplaySuccess($"Server '{serverName}' is available and responding.");
            else
                ConsoleHelper.DisplayError($"Server '{serverName}' is not available.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to test MCP server {ServerName}", serverName);
            ConsoleHelper.DisplayError($"Connection test failed: {ex.Message}");
        }
    }
}
