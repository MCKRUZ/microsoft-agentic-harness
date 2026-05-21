using Application.AI.Common.Interfaces;
using Microsoft.Extensions.Logging;
using Presentation.ConsoleUI.Common.Helpers;
using Spectre.Console;

namespace Presentation.ConsoleUI.Examples;

/// <summary>
/// Demonstrates the observability subsystem: budget tracking state machine (Clear/Warning/Critical),
/// session health scoring (Green/Yellow/Red), and agent configuration reporting.
/// Shows how LLM spend is tracked against thresholds and how agent health degrades with errors.
/// </summary>
public class ObservabilityBudgetExample
{
    private readonly IBudgetTrackingService _budgetService;
    private readonly ISessionHealthTracker _healthTracker;
    private readonly IAgentConfigReporter _configReporter;
    private readonly ILogger<ObservabilityBudgetExample> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ObservabilityBudgetExample"/> class.
    /// </summary>
    /// <param name="budgetService">Budget tracking and threshold management.</param>
    /// <param name="healthTracker">Session health scoring per agent.</param>
    /// <param name="configReporter">Agent configuration snapshot reporting.</param>
    /// <param name="logger">Logger instance.</param>
    public ObservabilityBudgetExample(
        IBudgetTrackingService budgetService,
        ISessionHealthTracker healthTracker,
        IAgentConfigReporter configReporter,
        ILogger<ObservabilityBudgetExample> logger)
    {
        _budgetService = budgetService;
        _healthTracker = healthTracker;
        _configReporter = configReporter;
        _logger = logger;
    }

    /// <summary>
    /// Runs the observability budget example with 3 demonstration steps.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ConsoleHelper.DisplayHeader("Observability: Budget Tracking & Health Scoring", Color.Cyan);
            AnsiConsole.WriteLine();
            ConsoleHelper.DisplayModeInfo(isLive: false, "Using in-memory budget and health tracking");
            AnsiConsole.WriteLine();

            await Step1_AgentConfigReportingAsync(cancellationToken);
            await Step2_SessionHealthTrackingAsync(cancellationToken);
            await Step3_BudgetTrackingAsync(cancellationToken);

            ConsoleHelper.DisplaySuccess("All observability demonstrations completed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during observability budget example");
            ConsoleHelper.DisplayError($"Example failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Step 1: Register an agent configuration and display its snapshot.
    /// </summary>
    private async Task Step1_AgentConfigReportingAsync(CancellationToken cancellationToken)
    {
        ConsoleHelper.DisplayStep(1, 3, "Agent Configuration Reporting");
        AnsiConsole.WriteLine();

        const string agentName = "demo-agent";
        const string model = "gpt-4o";
        const string temperature = "0.7";
        const int toolsCount = 5;
        const int skillsCount = 3;
        const int mcpServersCount = 2;

        _configReporter.RegisterAgent(agentName, model, temperature, toolsCount, skillsCount, mcpServersCount);

        var table = new Table().Border(TableBorder.Rounded);
        table.Title = new TableTitle("[bold cornflowerblue]Agent Configuration[/]");
        table.AddColumn("[bold]Property[/]");
        table.AddColumn("[bold]Value[/]");

        table.AddRow("Agent Name", $"[yellow]{agentName}[/]");
        table.AddRow("Model", $"[cyan]{model}[/]");
        table.AddRow("Temperature", temperature);
        table.AddRow("Tools Registered", toolsCount.ToString());
        table.AddRow("Skills Available", skillsCount.ToString());
        table.AddRow("MCP Servers", mcpServersCount.ToString());

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        _logger.LogInformation("Agent {AgentName} configuration registered: {Model}, temp={Temperature}, tools={ToolsCount}, skills={SkillsCount}, mcp={McpServersCount}",
            agentName, model, temperature, toolsCount, skillsCount, mcpServersCount);

        await Task.CompletedTask;
    }

    /// <summary>
    /// Step 2: Simulate agent turn success/failure and track health progression through color states.
    /// Health scores: 0=red, 1=yellow, 2=green.
    /// </summary>
    private async Task Step2_SessionHealthTrackingAsync(CancellationToken cancellationToken)
    {
        ConsoleHelper.DisplayStep(2, 3, "Session Health Tracking");
        AnsiConsole.WriteLine();

        const string agentName = "demo-agent";

        var table = new Table().Border(TableBorder.Rounded);
        table.Title = new TableTitle("[bold cornflowerblue]Health Progression[/]");
        table.AddColumn("[bold]Action[/]");
        table.AddColumn("[bold]Status[/]");
        table.AddColumn("[bold]Expected Health[/]");

        // Record 5 successes → GREEN (2)
        for (int i = 0; i < 5; i++)
        {
            _healthTracker.RecordSuccess(agentName);
        }
        table.AddRow("5 successes", "[green]GREEN[/]", "[green]2 - Healthy[/]");

        // Record 3 errors → YELLOW (1)
        for (int i = 0; i < 3; i++)
        {
            _healthTracker.RecordError(agentName);
        }
        table.AddRow("3 errors", "[yellow]YELLOW[/]", "[yellow]1 - Degraded[/]");

        // Record 5 more errors → RED (0)
        for (int i = 0; i < 5; i++)
        {
            _healthTracker.RecordError(agentName);
        }
        table.AddRow("5 more errors", "[red]RED[/]", "[red]0 - Erroring[/]");

        // Record 10 successes → GREEN (2) recovered
        for (int i = 0; i < 10; i++)
        {
            _healthTracker.RecordSuccess(agentName);
        }
        table.AddRow("10 successes", "[green]GREEN[/]", "[green]2 - Healthy[/]");

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        _logger.LogInformation("Agent {AgentName} health progression: 5 success (green) -> 3 errors (yellow) -> 5 errors (red) -> 10 success (green recovered)",
            agentName);

        await Task.CompletedTask;
    }

    /// <summary>
    /// Step 3: Record incremental spend amounts and track budget state transitions.
    /// Budget states: 0=clear, 1=warning, 2=critical.
    /// </summary>
    private async Task Step3_BudgetTrackingAsync(CancellationToken cancellationToken)
    {
        ConsoleHelper.DisplayStep(3, 3, "Budget Tracking: Daily & Monthly Thresholds");
        AnsiConsole.WriteLine();

        const string agentName = "demo-agent";
        const string dailyPeriod = "daily";
        const string monthlyPeriod = "monthly";

        // Show thresholds
        var thresholdTable = new Table().Border(TableBorder.Rounded);
        thresholdTable.Title = new TableTitle("[bold cornflowerblue]Budget Thresholds (USD)[/]");
        thresholdTable.AddColumn("[bold]Period[/]");
        thresholdTable.AddColumn("[bold]Warning[/]");
        thresholdTable.AddColumn("[bold]Critical[/]");

        double dailyWarning = _budgetService.GetThreshold(dailyPeriod, "warning");
        double dailyCritical = _budgetService.GetThreshold(dailyPeriod, "critical");
        double monthlyWarning = _budgetService.GetThreshold(monthlyPeriod, "warning");
        double monthlyCritical = _budgetService.GetThreshold(monthlyPeriod, "critical");

        thresholdTable.AddRow("Daily", $"${dailyWarning:F2}", $"${dailyCritical:F2}");
        thresholdTable.AddRow("Monthly", $"${monthlyWarning:F2}", $"${monthlyCritical:F2}");

        AnsiConsole.Write(thresholdTable);
        AnsiConsole.WriteLine();

        // Record incremental spends
        var spendTable = new Table().Border(TableBorder.Rounded);
        spendTable.Title = new TableTitle("[bold cornflowerblue]Spend Progression[/]");
        spendTable.AddColumn("[bold]Amount (USD)[/]");
        spendTable.AddColumn("[bold]Daily Spend[/]");
        spendTable.AddColumn("[bold]Daily Status[/]");
        spendTable.AddColumn("[bold]Monthly Spend[/]");
        spendTable.AddColumn("[bold]Monthly Status[/]");

        double[] spendAmounts = { 0.50, 1.00, 2.00, 5.00, 10.00 };

        foreach (double amount in spendAmounts)
        {
            _budgetService.RecordSpend(amount, agentName);

            double dailySpend = _budgetService.GetCurrentSpend(dailyPeriod);
            int dailyStatus = _budgetService.GetCurrentStatus(dailyPeriod);
            string dailyStatusText = dailyStatus switch
            {
                0 => "[green]CLEAR[/]",
                1 => "[yellow]WARNING[/]",
                2 => "[red]CRITICAL[/]",
                _ => "[grey]UNKNOWN[/]"
            };

            double monthlySpend = _budgetService.GetCurrentSpend(monthlyPeriod);
            int monthlyStatus = _budgetService.GetCurrentStatus(monthlyPeriod);
            string monthlyStatusText = monthlyStatus switch
            {
                0 => "[green]CLEAR[/]",
                1 => "[yellow]WARNING[/]",
                2 => "[red]CRITICAL[/]",
                _ => "[grey]UNKNOWN[/]"
            };

            spendTable.AddRow(
                $"+${amount:F2}",
                $"${dailySpend:F2}",
                dailyStatusText,
                $"${monthlySpend:F2}",
                monthlyStatusText);

            _logger.LogInformation("Agent {AgentName} spend recorded: +${Amount:F2} | Daily: ${DailySpend:F2} ({DailyStatus}) | Monthly: ${MonthlySpend:F2} ({MonthlyStatus})",
                agentName, amount, dailySpend, dailyStatusText, monthlySpend, monthlyStatusText);
        }

        AnsiConsole.Write(spendTable);
        AnsiConsole.WriteLine();

        await Task.CompletedTask;
    }
}
