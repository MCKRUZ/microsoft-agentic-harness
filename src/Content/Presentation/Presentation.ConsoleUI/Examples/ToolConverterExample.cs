using Application.AI.Common.Interfaces.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Presentation.ConsoleUI.Common.Helpers;
using Spectre.Console;

namespace Presentation.ConsoleUI.Examples;

/// <summary>
/// Demonstrates the tool conversion pipeline: resolves keyed <see cref="ITool"/>
/// instances from DI, displays their metadata, and converts them to
/// <c>Microsoft.Extensions.AI.AITool</c> for LLM function calling.
/// </summary>
public class ToolConverterExample
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IEnumerable<IToolConverter> _toolConverters;
    private readonly ILogger<ToolConverterExample> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ToolConverterExample"/> class.
    /// </summary>
    /// <param name="serviceProvider">Service provider for resolving keyed tool instances.</param>
    /// <param name="toolConverters">Registered tool converters ordered by priority.</param>
    /// <param name="logger">Logger instance.</param>
    public ToolConverterExample(
        IServiceProvider serviceProvider,
        IEnumerable<IToolConverter> toolConverters,
        ILogger<ToolConverterExample> logger)
    {
        _serviceProvider = serviceProvider;
        _toolConverters = toolConverters;
        _logger = logger;
    }

    /// <summary>
    /// Runs the interactive tool converter demo.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        ConsoleHelper.DisplayHeader("Tool Converter Demo", Color.Orange1);

        var knownToolKeys = new[] { "file_system", "calculation_engine", "document_search" };
        var resolvedTools = ResolveTools(knownToolKeys);

        if (resolvedTools.Count == 0)
        {
            ConsoleHelper.DisplayInfo("Tools", "No keyed ITool instances found in DI. Register tools with AddKeyedSingleton<ITool>(\"name\", ...).");
            return;
        }

        DisplayToolTable(resolvedTools);

        var toolName = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Select a tool to convert:[/]")
                .AddChoices(resolvedTools.Select(t => t.Name).Append("Back")));

        if (toolName == "Back") return;

        var tool = resolvedTools.First(t => t.Name == toolName);
        await ConvertToolAsync(tool);
    }

    private List<ITool> ResolveTools(IReadOnlyList<string> toolKeys)
    {
        var tools = new List<ITool>();

        foreach (var key in toolKeys)
        {
            try
            {
                var tool = _serviceProvider.GetKeyedService<ITool>(key);
                if (tool is not null)
                    tools.Add(tool);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not resolve keyed tool '{ToolKey}'", key);
            }
        }

        return tools;
    }

    private static void DisplayToolTable(IReadOnlyList<ITool> tools)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("[bold]Name[/]");
        table.AddColumn("[bold]Description[/]");
        table.AddColumn("[bold]Operations[/]");

        foreach (var tool in tools)
        {
            table.AddRow(
                $"[cyan]{Markup.Escape(tool.Name)}[/]",
                Markup.Escape(tool.Description),
                string.Join(", ", tool.SupportedOperations.Select(o => $"[grey]{Markup.Escape(o)}[/]")));
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private Task ConvertToolAsync(ITool tool)
    {
        try
        {
            var converter = _toolConverters
                .OrderBy(c => c.Priority)
                .FirstOrDefault(c => c.CanConvert(tool));

            if (converter is null)
            {
                ConsoleHelper.DisplayError($"No converter found that can handle tool '{tool.Name}'.");
                return Task.CompletedTask;
            }

            var aiTool = converter.Convert(tool);

            if (aiTool is null)
            {
                ConsoleHelper.DisplayError($"Converter returned null for tool '{tool.Name}'.");
                return Task.CompletedTask;
            }

            var aiToolName = aiTool is AIFunction fn ? fn.Name : tool.Name;
            var aiToolDescription = aiTool is AIFunction fnDesc ? fnDesc.Description : tool.Description;

            var resultTable = new Table().Border(TableBorder.Rounded);
            resultTable.AddColumn("[bold]Property[/]");
            resultTable.AddColumn("[bold]Value[/]");

            resultTable.AddRow("Source Tool", $"[cyan]{Markup.Escape(tool.Name)}[/]");
            resultTable.AddRow("AITool Name", $"[green]{Markup.Escape(aiToolName)}[/]");
            resultTable.AddRow("AITool Description", Markup.Escape(aiToolDescription ?? "(none)"));
            resultTable.AddRow("Conversion", "[green]Succeeded[/]");

            AnsiConsole.Write(resultTable);
            AnsiConsole.WriteLine();

            ConsoleHelper.DisplayInfo(
                "Next Step",
                "In a full agent pipeline, this AITool would be passed to the LLM for function calling.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool conversion failed for {ToolName}", tool.Name);
            ConsoleHelper.DisplayError($"Conversion failed: {ex.Message}");
        }

        return Task.CompletedTask;
    }
}
